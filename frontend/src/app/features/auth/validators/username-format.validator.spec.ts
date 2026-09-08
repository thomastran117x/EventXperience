import { FormControl } from '@angular/forms';

import {
  RESERVED_USERNAMES,
  USERNAME_FORMAT_HINT,
  describeUsernameProblem,
  isValidUsernameFormat,
  isWellFormedUsername,
  normalizeUsername,
  suggestUsernameFromEmail,
  usernameFormatValidator,
} from './username-format.validator';

/**
 * These tables mirror `UsernamePolicyTests` on the server. When one changes the other must too,
 * or the form will accept values the API rejects (or refuse ones it would have taken).
 */
describe('isWellFormedUsername', () => {
  const accepted = ['abc', 'a-b_c.d', 'user22', 'a'.repeat(50)];
  const rejected = [
    'ab',
    'a'.repeat(51),
    'événement',
    'a b',
    'a@b',
    '.ab',
    'ab-',
    'a..b',
    'a._b',
    'a__b',
  ];

  for (const value of accepted) {
    it(`accepts ${JSON.stringify(value)}`, () => {
      expect(isWellFormedUsername(value)).toBeTrue();
    });
  }

  for (const value of rejected) {
    it(`rejects ${JSON.stringify(value)}`, () => {
      expect(isWellFormedUsername(value)).toBeFalse();
    });
  }
});

describe('isValidUsernameFormat', () => {
  it('rejects reserved names that are otherwise well formed', () => {
    expect(isWellFormedUsername('admin')).toBeTrue();
    expect(isValidUsernameFormat('admin')).toBeFalse();
  });

  it('accepts a well-formed unreserved name', () => {
    expect(isValidUsernameFormat('adalovelace')).toBeTrue();
  });
});

describe('RESERVED_USERNAMES', () => {
  // Pinned so a change here is deliberate and gets mirrored into UsernamePolicy.ReservedNames.
  it('matches the server-side list', () => {
    expect([...RESERVED_USERNAMES].sort()).toEqual([
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
  });
});

describe('describeUsernameProblem', () => {
  it('reports the length rules separately from the charset rules', () => {
    expect(describeUsernameProblem('ab')).toBe('Username must be at least 3 characters.');
    expect(describeUsernameProblem('a'.repeat(51))).toBe(
      'Username must be 50 characters or fewer.',
    );
  });

  it('reports one combined message for charset and placement', () => {
    expect(describeUsernameProblem('a..b')).toBe(USERNAME_FORMAT_HINT);
    expect(describeUsernameProblem('.ab')).toBe(USERNAME_FORMAT_HINT);
  });

  // Deliberately vague, as on the server: naming the list would say which handles to hunt for.
  it('does not reveal that a name is reserved', () => {
    expect(describeUsernameProblem('admin')).toBe('That username is not available.');
  });

  it('reports nothing for an acceptable name', () => {
    expect(describeUsernameProblem('adalovelace')).toBeNull();
  });
});

describe('usernameFormatValidator', () => {
  function validate(value: string): string | null {
    return usernameFormatValidator(new FormControl(value))?.['usernameFormat']?.message ?? null;
  }

  it('leaves an empty field to Validators.required', () => {
    expect(usernameFormatValidator(new FormControl(''))).toBeNull();
    expect(usernameFormatValidator(new FormControl(null))).toBeNull();
  });

  // Validators.required accepts '   ' because its length is 3, so this validator has to own it.
  it('rejects a value that normalizes away to nothing', () => {
    expect(validate('   ')).toBe('Username is required.');
  });

  it('normalizes before judging, so casing and padding are not errors', () => {
    expect(validate('  AdaLovelace  ')).toBeNull();
  });

  it('carries the server wording in the error payload', () => {
    expect(validate('a..b')).toBe(USERNAME_FORMAT_HINT);
  });
});

describe('suggestUsernameFromEmail', () => {
  it('lowercases and keeps the legal characters of the local part', () => {
    expect(suggestUsernameFromEmail('Ada.Lovelace@example.com')).toBe('ada.lovelace');
  });

  it('collapses runs of separators and trims them from the edges', () => {
    expect(suggestUsernameFromEmail('..ada..lovelace..@example.com')).toBe('ada.lovelace');
  });

  it('drops characters that are not legal in a username', () => {
    expect(suggestUsernameFromEmail('ada+tag@example.com')).toBe('adatag');
  });

  it('truncates a long local part without leaving a trailing separator', () => {
    expect(suggestUsernameFromEmail(`${'a'.repeat(30)}.tail@example.com`)).toBe('a'.repeat(30));
  });

  // A guess that could not be submitted would prefill the field with an error, so return nothing.
  it('treats a missing address as empty', () => {
    expect(suggestUsernameFromEmail(undefined as unknown as string)).toBe('');
  });

  it('returns an empty string when no usable name can be derived', () => {
    expect(suggestUsernameFromEmail('ab@example.com')).toBe('');
    expect(suggestUsernameFromEmail('..@example.com')).toBe('');
    expect(suggestUsernameFromEmail('admin@example.com')).toBe('');
    expect(suggestUsernameFromEmail('')).toBe('');
  });

  it('produces a value the format validator accepts', () => {
    const suggestion = suggestUsernameFromEmail('Grace.Hopper@example.com');

    expect(suggestion).not.toBe('');
    expect(isValidUsernameFormat(normalizeUsername(suggestion))).toBeTrue();
  });
});
