import { AsyncValidatorFn } from '@angular/forms';
import { map } from 'rxjs/operators';
import { AuthService } from '../services/auth.service';
import { AvailabilityOutcome, availabilityValidator } from './availability.validator';

/** Matches the server-side EmailPolicy, so the check runs on the value the API will evaluate. */
export function normalizeEmail(value: string): string {
  return (value ?? '').trim().toLowerCase();
}

export type EmailAvailabilityOutcome = AvailabilityOutcome;

/** RFC 5321 practical limit, mirroring EmailPolicy.MaxLength. */
const MAX_LENGTH = 254;

/**
 * Mirrors the structural check EmailPolicy applies before it is worth a lookup: a local part, a
 * domain part, and exactly one separator. Deliberately not a full address parse — Validators.email
 * already owns the shape of the field, and this only avoids probes the API would answer with 400.
 */
function looksLikeAddress(email: string): boolean {
  const separator = email.indexOf('@');
  return separator > 0 && separator < email.length - 1 && email.indexOf('@', separator + 1) === -1;
}

/**
 * Reports `{ emailTaken: true }` when the API says an account already uses the address.
 *
 * See {@link availabilityValidator} for the debounce and fail-open behaviour this inherits. The
 * payoff for the user is being pointed at login before they fill in the rest of the form.
 */
export function emailAvailabilityValidator(
  auth: AuthService,
  onConfirmed: EmailAvailabilityOutcome = () => {},
): AsyncValidatorFn {
  return availabilityValidator(
    {
      normalize: normalizeEmail,
      isProbeable: (email) =>
        email.length > 0 && email.length <= MAX_LENGTH && looksLikeAddress(email),
      probe: (email) => auth.checkEmailAvailability(email).pipe(map((result) => result.available)),
      errorKey: 'emailTaken',
    },
    onConfirmed,
  );
}
