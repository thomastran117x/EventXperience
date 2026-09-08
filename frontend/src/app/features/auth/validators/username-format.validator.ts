import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/**
 * The client-side mirror of the server's `UsernamePolicy`
 * (`backend/src/main/features/profile/UsernamePolicy.cs`). The two must be changed together: this
 * file only decides what the form will let a user submit, and the server re-checks every value.
 *
 * The rules apply to values being *written*. Accounts created before the policy existed can hold
 * usernames that fail these checks, so nothing that merely looks a username up may use them.
 */

export const USERNAME_MIN_LENGTH = 3;
export const USERNAME_MAX_LENGTH = 50;

/**
 * Names withheld from signup because holding one lets an account pass for staff. Mirrors
 * `UsernamePolicy.ReservedNames`.
 */
export const RESERVED_USERNAMES: ReadonlySet<string> = new Set([
  'admin',
  'administrator',
  'anonymous',
  'api',
  'moderator',
  'null',
  'official',
  'root',
  'security',
  'staff',
  'superuser',
  'support',
  'system',
  'undefined',
]);

/** The single message covering the charset and placement rules. Mirrors `UsernamePolicy.FormatMessage`. */
export const USERNAME_FORMAT_HINT =
  'Username may use only lowercase letters, numbers, and . _ -, ' +
  'must start and end with a letter or number, and cannot repeat . _ -.';

/** Matches the server-side UsernamePolicy, so the check runs on the value the API will evaluate. */
export function normalizeUsername(value: string): string {
  return (value ?? '').trim().toLowerCase();
}

function isAlphanumeric(character: string): boolean {
  return (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
}

function isSeparator(character: string): boolean {
  return character === '.' || character === '_' || character === '-';
}

/** Whether an already-normalised value satisfies the charset and placement rules. */
export function isWellFormedUsername(normalized: string): boolean {
  if (normalized.length < USERNAME_MIN_LENGTH || normalized.length > USERNAME_MAX_LENGTH) {
    return false;
  }

  if (!isAlphanumeric(normalized[0]) || !isAlphanumeric(normalized[normalized.length - 1])) {
    return false;
  }

  let previousWasSeparator = false;
  for (const character of normalized) {
    if (isAlphanumeric(character)) {
      previousWasSeparator = false;
      continue;
    }

    if (!isSeparator(character) || previousWasSeparator) {
      return false;
    }

    previousWasSeparator = true;
  }

  return true;
}

/** Whether a normalised value is one the API would accept. Used to gate the availability probe. */
export function isValidUsernameFormat(normalized: string): boolean {
  return isWellFormedUsername(normalized) && !RESERVED_USERNAMES.has(normalized);
}

/**
 * The reason a normalised value would be rejected, phrased exactly as the server phrases it, or
 * null when it would be accepted.
 */
export function describeUsernameProblem(normalized: string): string | null {
  if (normalized.length < USERNAME_MIN_LENGTH) {
    return `Username must be at least ${USERNAME_MIN_LENGTH} characters.`;
  }

  if (normalized.length > USERNAME_MAX_LENGTH) {
    return `Username must be ${USERNAME_MAX_LENGTH} characters or fewer.`;
  }

  if (!isWellFormedUsername(normalized)) {
    return USERNAME_FORMAT_HINT;
  }

  // Deliberately vague, as on the server: naming the list would say which handles to hunt for.
  if (RESERVED_USERNAMES.has(normalized)) {
    return 'That username is not available.';
  }

  return null;
}

/**
 * Reports `{ usernameFormat: { message } }` for a value the API would reject.
 *
 * Carries the message in the payload rather than splitting into one error key per rule, so a
 * template needs a single branch to say the same thing the server would have said.
 */
export const usernameFormatValidator: ValidatorFn = (
  control: AbstractControl,
): ValidationErrors | null => {
  const raw = control.value ?? '';

  // Validators.required owns a genuinely empty field; reporting both would stack two messages.
  if (raw.length === 0) {
    return null;
  }

  // Validators.required does not: '   ' has length 3 and passes it, but normalises to nothing.
  const normalized = normalizeUsername(raw);
  if (normalized.length === 0) {
    return { usernameFormat: { message: 'Username is required.' } };
  }

  const message = describeUsernameProblem(normalized);
  return message ? { usernameFormat: { message } } : null;
};

/**
 * A starting username derived from an email local part, for prefilling the OAuth signup step.
 *
 * Returns '' rather than a guess that could not be submitted, so a name we cannot build cleanly
 * leaves the field empty instead of pre-loading it with an error.
 */
export function suggestUsernameFromEmail(email: string): string {
  const localPart = (email ?? '').trim().toLowerCase().split('@')[0] ?? '';

  let suggestion = '';
  for (const character of localPart) {
    if (isAlphanumeric(character)) {
      suggestion += character;
      continue;
    }

    // Collapse runs of separators, and never open with one.
    if (isSeparator(character) && suggestion.length > 0 && !isSeparator(suggestion.slice(-1))) {
      suggestion += character;
    }
  }

  // Leave room under the cap for the disambiguating suffix a user is likely to add.
  suggestion = suggestion.slice(0, 30);
  while (suggestion.length > 0 && isSeparator(suggestion.slice(-1))) {
    suggestion = suggestion.slice(0, -1);
  }

  return isValidUsernameFormat(suggestion) ? suggestion : '';
}
