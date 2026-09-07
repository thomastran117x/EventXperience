import { AbstractControl, AsyncValidatorFn, ValidationErrors } from '@angular/forms';
import { Observable, of, timer } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';

/**
 * Notified only when the API actually answered. A failed or skipped probe reports `null`, so the
 * UI can distinguish "confirmed free" from "we never found out".
 */
export type AvailabilityOutcome = (value: string | null) => void;

export const DEBOUNCE_MS = 400;

export interface AvailabilityProbeConfig {
  /** Must mirror the server-side policy, so the check runs on the value the API will evaluate. */
  normalize: (value: string) => string;
  /** False for values the synchronous validators already own; the API is not called for those. */
  isProbeable: (normalized: string) => boolean;
  /** Issues the request. Emits true when the value is still free. */
  probe: (normalized: string) => Observable<boolean>;
  /** Error key reported when the value is already taken. */
  errorKey: string;
}

/**
 * Shared machinery behind the username and email availability validators: debounce, normalise,
 * probe, and report.
 *
 * The check is advisory. It reserves nothing, and signup can still come back with a conflict if
 * someone claims the value in between. A failed or unreachable request resolves to "no error"
 * rather than blocking submission — the server rejects a duplicate regardless, so failing open
 * costs a late error message instead of a form nobody can submit.
 *
 * Because failing open leaves the control VALID, callers must not read validity as proof the value
 * is free; `onConfirmed` is the only signal that a real answer came back.
 */
export function availabilityValidator(
  config: AvailabilityProbeConfig,
  onConfirmed: AvailabilityOutcome = () => {},
): AsyncValidatorFn {
  return (control: AbstractControl): Observable<ValidationErrors | null> => {
    const value = config.normalize(control.value);

    // Let the synchronous validators own these cases; probing the API would only add noise.
    if (!config.isProbeable(value)) {
      onConfirmed(null);
      return of(null);
    }

    // timer + switchMap debounces per keystroke: Angular resubscribes on every change, which
    // cancels the pending timer before the request is ever issued.
    return timer(DEBOUNCE_MS).pipe(
      switchMap(() => config.probe(value)),
      map((available) => {
        onConfirmed(available ? value : null);
        return available ? null : { [config.errorKey]: true };
      }),
      catchError(() => {
        onConfirmed(null);
        return of(null);
      }),
    );
  };
}
