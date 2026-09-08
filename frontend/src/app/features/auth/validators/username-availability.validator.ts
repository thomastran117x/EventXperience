import { AsyncValidatorFn } from '@angular/forms';
import { map } from 'rxjs/operators';
import { AuthService } from '../services/auth.service';
import { AvailabilityOutcome, availabilityValidator } from './availability.validator';
import { isValidUsernameFormat, normalizeUsername } from './username-format.validator';

// Re-exported so callers can reach the shared normalisation without knowing which file defines it.
export { normalizeUsername };

export type UsernameAvailabilityOutcome = AvailabilityOutcome;

export interface UsernameAvailabilityOptions {
  /**
   * A username to treat as free without asking. Supply the account's current name on a rename
   * form: it is theirs already, so the API would report it taken, and prefilling the field would
   * otherwise spend a probe on every page load.
   */
  exempt?: () => string | null;
}

/**
 * Reports `{ usernameTaken: true }` when the API says the name is already spoken for.
 *
 * Only well-formed, unreserved values are probed — `/auth/username/availability` answers 400 for
 * anything else, and its rate limit is 30/min/IP, so sending one would spend budget to learn
 * nothing the synchronous validator did not already know.
 *
 * See {@link availabilityValidator} for the debounce and fail-open behaviour this inherits.
 */
export function usernameAvailabilityValidator(
  auth: AuthService,
  onConfirmed: UsernameAvailabilityOutcome = () => {},
  options: UsernameAvailabilityOptions = {},
): AsyncValidatorFn {
  return availabilityValidator(
    {
      normalize: normalizeUsername,
      isProbeable: (username) =>
        isValidUsernameFormat(username) && username !== normalizeUsername(options.exempt?.() ?? ''),
      probe: (username) =>
        auth.checkUsernameAvailability(username).pipe(map((result) => result.available)),
      errorKey: 'usernameTaken',
    },
    onConfirmed,
  );
}
