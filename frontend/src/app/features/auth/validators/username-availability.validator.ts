import { AsyncValidatorFn } from '@angular/forms';
import { map } from 'rxjs/operators';
import { AuthService } from '../services/auth.service';
import { AvailabilityOutcome, availabilityValidator } from './availability.validator';

/** Matches the server-side UsernamePolicy, so the check runs on the value the API will evaluate. */
export function normalizeUsername(value: string): string {
  return (value ?? '').trim().toLowerCase();
}

export type UsernameAvailabilityOutcome = AvailabilityOutcome;

const MAX_LENGTH = 50;

/**
 * Reports `{ usernameTaken: true }` when the API says the name is already spoken for.
 *
 * See {@link availabilityValidator} for the debounce and fail-open behaviour this inherits.
 */
export function usernameAvailabilityValidator(
  auth: AuthService,
  onConfirmed: UsernameAvailabilityOutcome = () => {},
): AsyncValidatorFn {
  return availabilityValidator(
    {
      normalize: normalizeUsername,
      isProbeable: (username) => username.length > 0 && username.length <= MAX_LENGTH,
      probe: (username) =>
        auth.checkUsernameAvailability(username).pipe(map((result) => result.available)),
      errorKey: 'usernameTaken',
    },
    onConfirmed,
  );
}
