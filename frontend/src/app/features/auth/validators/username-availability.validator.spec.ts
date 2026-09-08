import { fakeAsync, tick } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { Observable, of, throwError } from 'rxjs';

import { AuthService, UsernameAvailabilityResponse } from '../services/auth.service';
import {
  UsernameAvailabilityOptions,
  normalizeUsername,
  usernameAvailabilityValidator,
} from './username-availability.validator';

describe('usernameAvailabilityValidator', () => {
  function createAuth(
    result: Observable<UsernameAvailabilityResponse>,
  ): jasmine.SpyObj<AuthService> {
    const auth = jasmine.createSpyObj<AuthService>('AuthService', ['checkUsernameAvailability']);
    auth.checkUsernameAvailability.and.returnValue(result);
    return auth;
  }

  function runValidator(
    auth: jasmine.SpyObj<AuthService>,
    value: string,
    options: UsernameAvailabilityOptions = {},
  ): { errors: unknown; confirmed: string | null | undefined } {
    const control = new FormControl(value);
    const captured: { errors: unknown; confirmed: string | null | undefined } = {
      errors: undefined,
      confirmed: undefined,
    };

    const validator = usernameAvailabilityValidator(
      auth,
      (username) => {
        captured.confirmed = username;
      },
      options,
    );

    (validator(control) as Observable<unknown>).subscribe((errors) => {
      captured.errors = errors;
    });

    return captured;
  }

  it('works without a confirmation callback', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: false }));
    let errors: unknown;

    (usernameAvailabilityValidator(auth)(new FormControl('ada')) as Observable<unknown>).subscribe(
      (result) => (errors = result),
    );
    tick(400);

    expect(errors).toEqual({ usernameTaken: true });
  }));

  it('reports no error when the username is available', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.errors).toBeNull();
    expect(auth.checkUsernameAvailability).toHaveBeenCalledWith('ada');
  }));

  it('reports usernameTaken when the name is already claimed', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: false }));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.errors).toEqual({ usernameTaken: true });
  }));

  it('normalizes the value before asking the API', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    runValidator(auth, '  AdaLovelace  ');
    tick(400);

    expect(auth.checkUsernameAvailability).toHaveBeenCalledWith('adalovelace');
  }));

  it('does not call the API before the debounce elapses', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    runValidator(auth, 'ada');
    tick(399);
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();

    tick(1);
    expect(auth.checkUsernameAvailability).toHaveBeenCalledTimes(1);
  }));

  it('skips the API for values the synchronous validators already reject', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    const empty = runValidator(auth, '   ');
    const tooLong = runValidator(auth, 'a'.repeat(51));
    tick(400);

    expect(empty.errors).toBeNull();
    expect(tooLong.errors).toBeNull();
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();
  }));

  // The endpoint answers 400 for these, and its rate limit is 30/min/IP, so a probe would spend
  // budget to learn nothing the format validator did not already know.
  it('skips the API for values the endpoint would reject', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    for (const value of ['ab', 'a..b', '.ab', 'ab-', 'a b', 'admin']) {
      expect(runValidator(auth, value).errors).toBeNull();
    }
    tick(400);

    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();
  }));

  // On a rename form the field is prefilled with the account's current name, which the API would
  // report as taken. Probing it would also spend a request on every profile load.
  it('skips the API for an exempt username', fakeAsync(() => {
    const auth = createAuth(of({ username: 'member', available: false }));

    const result = runValidator(auth, '  Member  ', { exempt: () => 'member' });
    tick(400);

    expect(result.errors).toBeNull();
    expect(result.confirmed).toBeNull();
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();
  }));

  it('still probes a value that differs from the exempt username', fakeAsync(() => {
    const auth = createAuth(of({ username: 'other', available: true }));

    runValidator(auth, 'other', { exempt: () => 'member' });
    tick(400);

    expect(auth.checkUsernameAvailability).toHaveBeenCalledWith('other');
  }));

  // The server rejects duplicates regardless, so failing open costs a late error message rather
  // than a form the user cannot submit.
  it('fails open when the request errors', fakeAsync(() => {
    const auth = createAuth(throwError(() => new Error('network down')));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.errors).toBeNull();
  }));

  // The UI must be able to tell "confirmed free" from "we never found out"; without this the
  // signup form announces "That username is available" after a failed or throttled probe.
  it('confirms the username only when the API actually answered', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.confirmed).toBe('ada');
  }));

  it('reports no confirmation when the name is taken', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: false }));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.confirmed).toBeNull();
  }));

  it('reports no confirmation when the request fails', fakeAsync(() => {
    const auth = createAuth(throwError(() => new Error('network down')));

    const result = runValidator(auth, 'ada');
    tick(400);

    expect(result.errors).toBeNull();
    expect(result.confirmed).toBeNull();
  }));

  it('reports no confirmation for values it never probes', fakeAsync(() => {
    const auth = createAuth(of({ username: 'ada', available: true }));

    const result = runValidator(auth, '   ');
    tick(400);

    expect(result.confirmed).toBeNull();
  }));
});

describe('normalizeUsername', () => {
  it('trims and lowercases to match the server-side policy', () => {
    expect(normalizeUsername('  AdaLovelace ')).toBe('adalovelace');
  });

  it('treats a missing value as empty', () => {
    expect(normalizeUsername(undefined as unknown as string)).toBe('');
  });
});
