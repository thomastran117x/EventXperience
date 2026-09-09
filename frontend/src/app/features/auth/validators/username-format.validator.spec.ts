import { FormControl } from '@angular/forms';

import {
  RESERVED_USERNAMES,
  USERNAME_FORMAT_HINT,
  describeUsernameProblem,
  isValidUsernameFormat,
  isWellFormedUsername,
  isDisplayCharset,
  normalizeUsername,
  toUsernameDisplay,
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

describe('toUsernameDisplay', () => {
  it('trims but does not lowercase, so the server can keep the casing', () => {
    expect(toUsernameDisplay('  ThomasT  ')).toBe('ThomasT');
    expect(toUsernameDisplay('SmartCat23')).toBe('SmartCat23');
  });

  it('normalises to the same value the availability probe uses', () => {
    expect(normalizeUsername(toUsernameDisplay('  ThomasT  '))).toBe('thomast');
  });

  it('treats a missing value as empty rather than throwing', () => {
    expect(toUsernameDisplay(null as unknown as string)).toBe('');
    expect(toUsernameDisplay(undefined as unknown as string)).toBe('');
  });
});

describe('mixed case', () => {
  it('accepts capitals, which the validator has always normalised away rather than rejected', () => {
    expect(describeUsernameProblem(normalizeUsername('ThomasT'))).toBeNull();
  });

  it('no longer tells the user their username must be lowercase', () => {
    expect(USERNAME_FORMAT_HINT).not.toContain('lowercase');
  });
});

describe('homoglyph display characters', () => {
  // U+212A KELVIN SIGN lowercases to an ASCII 'k', so the normalised form is a clean 'kelvin' and
  // every rule that inspects it passes — while the value the server would store is not ASCII.
  const kelvin = 'Kelvin';

  it('normalises to something that looks entirely valid', () => {
    expect(normalizeUsername(kelvin)).toBe('kelvin');
    expect(describeUsernameProblem(normalizeUsername(kelvin))).toBeNull();
  });

  it('is rejected anyway, because the stored form is checked too', () => {
    const control = new FormControl(kelvin);

    expect(isDisplayCharset(kelvin)).toBeFalse();
    expect(usernameFormatValidator(control)).toEqual({
      usernameFormat: { message: USERNAME_FORMAT_HINT },
    });
  });

  it('still accepts ordinary mixed case and separators', () => {
    expect(isDisplayCharset('SmartCat23')).toBeTrue();
    expect(isDisplayCharset('a.b_c-d')).toBeTrue();
    expect(usernameFormatValidator(new FormControl('SmartCat23'))).toBeNull();
  });
});
