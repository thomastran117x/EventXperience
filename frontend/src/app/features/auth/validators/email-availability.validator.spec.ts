import { fakeAsync, tick } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { Observable, of, throwError } from 'rxjs';

import { AuthService, EmailAvailabilityResponse } from '../services/auth.service';
import { emailAvailabilityValidator, normalizeEmail } from './email-availability.validator';

describe('emailAvailabilityValidator', () => {
  function createAuth(result: Observable<EmailAvailabilityResponse>): jasmine.SpyObj<AuthService> {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['checkEmailAvailability']);
    auth.checkEmailAvailability.and.returnValue(result);
    return auth;
  }

  function runValidator(
    auth: jasmine.SpyObj<AuthService>,
    value: string,
  ): { errors: unknown; confirmed: string | null | undefined } {
    const control = new FormControl(value);
    const captured: { errors: unknown; confirmed: string | null | undefined } = {
      errors: undefined,
      confirmed: undefined,
    };

    const validator = emailAvailabilityValidator(auth, (email) => {
      captured.confirmed = email;
    });

    (validator(control) as Observable<unknown>).subscribe((errors) => {
      captured.errors = errors;
    });

    return captured;
  }

  it('reports no error when the address is unregistered', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.errors).toBeNull();
    expect(auth.checkEmailAvailability).toHaveBeenCalledWith('ada@example.com');
  }));

  it('reports emailTaken when an account already uses the address', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: false }));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.errors).toEqual({ emailTaken: true });
  }));

  // The bloom filter hashes the literal string, so the probe must send exactly what the server
  // seeded the filter with.
  it('normalizes the value before asking the API', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    runValidator(auth, '  Ada@Example.COM  ');
    tick(400);

    expect(auth.checkEmailAvailability).toHaveBeenCalledWith('ada@example.com');
  }));

  it('does not call the API before the debounce elapses', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    runValidator(auth, 'ada@example.com');
    tick(399);
    expect(auth.checkEmailAvailability).not.toHaveBeenCalled();

    tick(1);
    expect(auth.checkEmailAvailability).toHaveBeenCalledTimes(1);
  }));

  // Every skipped probe is a request the rate limiter does not have to spend on a value the API
  // would answer with a 400 anyway.
  it('skips the API for values the synchronous validators already reject', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    const empty = runValidator(auth, '   ');
    const noAt = runValidator(auth, 'not-an-address');
    const noLocalPart = runValidator(auth, '@example.com');
    const noDomain = runValidator(auth, 'ada@');
    const twoSeparators = runValidator(auth, 'ada@@example.com');
    const tooLong = runValidator(auth, `${'a'.repeat(250)}@example.com`);
    tick(400);

    expect(empty.errors).toBeNull();
    expect(noAt.errors).toBeNull();
    expect(noLocalPart.errors).toBeNull();
    expect(noDomain.errors).toBeNull();
    expect(twoSeparators.errors).toBeNull();
    expect(tooLong.errors).toBeNull();
    expect(auth.checkEmailAvailability).not.toHaveBeenCalled();
  }));

  // The server rejects duplicates regardless, so failing open costs a late error message rather
  // than a form the user cannot submit.
  it('fails open when the request errors', fakeAsync(() => {
    const auth = createAuth(throwError(() => new Error('network down')));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.errors).toBeNull();
  }));

  // Rate limiting is the expected failure here, not an outage: the endpoint allows 15 probes a
  // minute, and a throttled one must never block submission.
  it('fails open when the probe is rate limited', fakeAsync(() => {
    const auth = createAuth(throwError(() => ({ status: 429 })));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.errors).toBeNull();
    expect(result.confirmed).toBeNull();
  }));

  // The UI must be able to tell "confirmed free" from "we never found out"; without this the
  // signup form announces "That email is available" after a failed or throttled probe.
  it('confirms the address only when the API actually answered', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.confirmed).toBe('ada@example.com');
  }));

  it('reports no confirmation when the address is registered', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: false }));

    const result = runValidator(auth, 'ada@example.com');
    tick(400);

    expect(result.confirmed).toBeNull();
  }));

  it('reports no confirmation for values it never probes', fakeAsync(() => {
    const auth = createAuth(of({ email: 'ada@example.com', available: true }));

    const result = runValidator(auth, 'not-an-address');
    tick(400);

    expect(result.confirmed).toBeNull();
  }));
});

describe('normalizeEmail', () => {
  it('trims and lowercases to match the server-side policy', () => {
    expect(normalizeEmail('  Ada@Example.COM ')).toBe('ada@example.com');
  });

  it('treats a missing value as empty', () => {
    expect(normalizeEmail(undefined as unknown as string)).toBe('');
  });
});
