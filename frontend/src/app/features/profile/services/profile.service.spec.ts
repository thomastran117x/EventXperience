import { HttpTestingController } from '@angular/common/http/testing';
import { fakeAsync, tick } from '@angular/core/testing';

import { environment } from '@environments/environment';
import { envelope, errorEnvelope, setupService } from '@testing';

import { MyProfile, ProfileService, PublicProfile } from './profile.service';
import { ApiClient } from '../../../core/api/services/api-client.service';
import { AuthTokenService } from '../../../core/api/services/auth-token.service';
import { ApiClientClientError } from '../../../core/api/models/api-client-error.model';

describe('ProfileService', () => {
  const base = `${environment.backendUrl}/profile`;
  const profile: MyProfile = {
    Id: 1,
    Email: 'member@example.com',
    Username: 'member',
    UsernameDisplay: 'member',
    CanChangeUsername: true,
    UsernameChangeAvailableAtUtc: null,
    Name: 'Test Member',
    Avatar: null,
    Usertype: 'User',
    Phone: null,
    Address: null,
    HasLocalPassword: true,
    GoogleLinked: false,
    MicrosoftLinked: false,
    CreatedAtUtc: '2026-01-01T00:00:00Z',
    UpdatedAtUtc: '2026-01-02T00:00:00Z',
  };

  let service: ProfileService;
  let httpMock: HttpTestingController;
  let ensureCsrfToken: jasmine.Spy<() => Promise<void>>;

  beforeEach(() => {
    ensureCsrfToken = jasmine.createSpy('ensureCsrfToken').and.resolveTo();

    ({ service, httpMock } = setupService(ProfileService, [
      ApiClient,
      { provide: AuthTokenService, useValue: { ensureCsrfToken } },
    ]));
  });

  afterEach(() => {
    httpMock.verify();
  });

  describe('email change', () => {
    it('sends the new address and current password to request a change', fakeAsync(() => {
      let challenge: { Challenge: string; ExpiresAtUtc: string } | undefined;
      service
        .requestEmailChange('new@example.com', 'Password123!')
        .subscribe((value) => (challenge = value));
      tick();

      const request = httpMock.expectOne(`${base}/email`);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.body).toEqual({
        newEmail: 'new@example.com',
        currentPassword: 'Password123!',
      });
      request.flush(
        envelope({ Challenge: 'challenge-token', ExpiresAtUtc: '2026-09-07T12:30:00Z' }),
      );

      expect(challenge?.Challenge).toBe('challenge-token');
    }));

    it('omits the password for an account that has none', fakeAsync(() => {
      service.requestEmailChange('new@example.com').subscribe();
      tick();

      const request = httpMock.expectOne(`${base}/email`);
      expect(request.request.body.currentPassword).toBeUndefined();
      request.flush(envelope({ Challenge: 'c', ExpiresAtUtc: '2026-09-07T12:30:00Z' }));
    }));

    it('reads the pending change', fakeAsync(() => {
      let pending: { NewEmail: string } | null | undefined;
      service.getPendingEmailChange().subscribe((value) => (pending = value));
      tick();

      const request = httpMock.expectOne(`${base}/email/pending`);
      expect(request.request.method).toBe('GET');
      request.flush(
        envelope({ NewEmail: 'new@example.com', ExpiresAtUtc: '2026-09-07T12:30:00Z' }),
      );

      expect(pending?.NewEmail).toBe('new@example.com');
    }));

    // The endpoint answers 200 with a null payload rather than 404 when nothing is in flight.
    it('resolves to null when no change is pending', fakeAsync(() => {
      let pending: { NewEmail: string } | null | undefined = undefined;
      service.getPendingEmailChange().subscribe((value) => (pending = value));
      tick();

      httpMock.expectOne(`${base}/email/pending`).flush(envelope(null));

      expect(pending).toBeNull();
    }));

    it('cancels the pending change', fakeAsync(() => {
      let completed = false;
      service.cancelEmailChange().subscribe(() => (completed = true));
      tick();

      const request = httpMock.expectOne(`${base}/email/pending`);
      expect(request.request.method).toBe('DELETE');
      expect(request.request.withCredentials).toBeTrue();
      request.flush(null);

      expect(completed).toBeTrue();
    }));

    it('surfaces a conflict when the address is already taken', fakeAsync(() => {
      let error: unknown;
      service.requestEmailChange('taken@example.com', 'pw').subscribe({
        error: (err) => (error = err),
      });
      tick();

      httpMock
        .expectOne(`${base}/email`)
        .flush(errorEnvelope('EMAIL_TAKEN', 'That email is already in use.'), {
          status: 409,
          statusText: 'Conflict',
        });

      expect(error).toBeInstanceOf(ApiClientClientError);
      expect((error as ApiClientClientError).status).toBe(409);
    }));
  });

  it('bootstraps CSRF before reading the signed-in profile', fakeAsync(() => {
    let result: MyProfile | undefined;
    service.getMyProfile().subscribe((value) => (result = value));
    tick();

    expect(ensureCsrfToken).toHaveBeenCalledTimes(1);
    const request = httpMock.expectOne(base);
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(envelope(profile));

    expect(result).toEqual(profile);
  }));

  it('reads a public profile without CSRF and URL-encodes the username', () => {
    let result: unknown;
    service.getPublicProfile('jamie rivers/1').subscribe((value) => (result = value));

    expect(ensureCsrfToken).not.toHaveBeenCalled();
    const request = httpMock.expectOne(`${base}/jamie%20rivers%2F1`);
    expect(request.request.method).toBe('GET');
    request.flush(
      envelope({
        Username: 'jamie',
        UsernameDisplay: 'jamie',
        Name: 'Jamie',
        Avatar: null,
        Usertype: 'User',
        CreatedAtUtc: '2026-01-01T00:00:00Z',
      }),
    );

    expect(result).toEqual(jasmine.objectContaining({ Username: 'jamie' }));
  });

  it('patches only the supplied profile fields', fakeAsync(() => {
    service.updateProfile({ name: 'New Name', phone: '555-1111' }).subscribe();
    tick();

    const request = httpMock.expectOne(base);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ name: 'New Name', phone: '555-1111' });
    request.flush(envelope(profile));
  }));

  it('changes the username through the dedicated endpoint', fakeAsync(() => {
    service.changeUsername('new-member').subscribe();
    tick();

    const request = httpMock.expectOne(`${base}/username`);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({ username: 'new-member' });
    expect(request.request.withCredentials).toBeTrue();
    request.flush(envelope({ ...profile, Username: 'new-member' }));
  }));

  it('uploads the avatar as multipart form data', fakeAsync(() => {
    const file = new File(['bytes'], 'avatar.png', { type: 'image/png' });

    service.uploadAvatar(file).subscribe();
    tick();

    const request = httpMock.expectOne(`${base}/avatar`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body instanceof FormData).toBeTrue();
    expect((request.request.body as FormData).get('image')).toBe(file);
    request.flush(envelope(profile));
  }));

  it('posts both passwords to the change-password endpoint', fakeAsync(() => {
    service.changePassword('old-secret', 'new-secret').subscribe();
    tick();

    const request = httpMock.expectOne(`${base}/change-password`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      currentPassword: 'old-secret',
      newPassword: 'new-secret',
    });
    request.flush(null);
  }));

  it('deletes the account after bootstrapping CSRF', fakeAsync(() => {
    service.deleteAccount().subscribe();
    tick();

    expect(ensureCsrfToken).toHaveBeenCalledTimes(1);
    const request = httpMock.expectOne(base);
    expect(request.request.method).toBe('DELETE');
    expect(request.request.withCredentials).toBeTrue();
    request.flush(null);
  }));

  it('errors when the profile envelope carries no data', fakeAsync(() => {
    let thrown: Error | undefined;
    service.getMyProfile().subscribe({ error: (err: Error) => (thrown = err) });
    tick();

    httpMock.expectOne(base).flush(envelope(null, { message: '' }));

    expect(thrown?.message).toBe('Profile response was incomplete.');
  }));

  it('surfaces a 4xx as a typed client error', fakeAsync(() => {
    let thrown: unknown;
    service.changePassword('wrong', 'new').subscribe({ error: (err) => (thrown = err) });
    tick();

    httpMock
      .expectOne(`${base}/change-password`)
      .flush(errorEnvelope('INVALID_CREDENTIALS', 'Current password is incorrect.'), {
        status: 400,
        statusText: 'Bad Request',
      });

    expect(thrown).toEqual(jasmine.any(ApiClientClientError));
    expect((thrown as ApiClientClientError).code).toBe('INVALID_CREDENTIALS');
  }));

  /// The bug this normalizer exists for: the API serialises camelCase while MyProfile is declared
  /// PascalCase, so a raw cast read every field as undefined and the display casing never rendered.
  it('reads a camelCase profile payload, which is what the API actually sends', fakeAsync(() => {
    let received: MyProfile | undefined;
    service.getMyProfile().subscribe((value) => (received = value));
    tick();

    httpMock.expectOne(base).flush(
      envelope({
        id: 7,
        email: 'thomas@example.com',
        username: 'thomast',
        usernameDisplay: 'ThomasT',
        canChangeUsername: true,
        usernameChangeAvailableAtUtc: null,
        name: 'Thomas',
        avatar: null,
        usertype: 'Participant',
        phone: null,
        address: null,
        hasLocalPassword: true,
        googleLinked: false,
        microsoftLinked: false,
        createdAtUtc: '2026-01-01T00:00:00Z',
        updatedAtUtc: '2026-01-02T00:00:00Z',
      }),
    );
    tick();

    expect(received?.Id).toBe(7);
    expect(received?.Username).toBe('thomast');
    expect(received?.UsernameDisplay).toBe('ThomasT');
    expect(received?.CanChangeUsername).toBeTrue();
    expect(received?.Name).toBe('Thomas');
  }));

  it('still reads a PascalCase payload, so nothing that already worked breaks', fakeAsync(() => {
    let received: MyProfile | undefined;
    service.getMyProfile().subscribe((value) => (received = value));
    tick();

    httpMock.expectOne(base).flush(envelope(profile));
    tick();

    expect(received).toEqual(profile);
  }));

  /// An account created before the display column carries no display form; the lookup key is what
  /// used to be rendered and stays the correct fallback.
  it('falls back to the username when the payload carries no display form', fakeAsync(() => {
    let received: MyProfile | undefined;
    service.getMyProfile().subscribe((value) => (received = value));
    tick();

    const { UsernameDisplay, ...withoutDisplay } = profile;
    httpMock.expectOne(base).flush(envelope(withoutDisplay));
    tick();

    expect(received?.UsernameDisplay).toBe('member');
  }));

  it('reads a camelCase public profile and renders the display casing', fakeAsync(() => {
    let received: PublicProfile | undefined;
    service.getPublicProfile('thomast').subscribe((value) => (received = value));

    httpMock.expectOne(`${base}/thomast`).flush(
      envelope({
        username: 'thomast',
        usernameDisplay: 'ThomasT',
        name: null,
        avatar: null,
        usertype: 'Participant',
        createdAtUtc: '2026-01-01T00:00:00Z',
      }),
    );
    tick();

    expect(received?.Username).toBe('thomast');
    expect(received?.UsernameDisplay).toBe('ThomasT');
    expect(received?.Name).toBeNull();
  }));

  it('falls back to the username on a public profile with no display form', fakeAsync(() => {
    let received: PublicProfile | undefined;
    service.getPublicProfile('legacy').subscribe((value) => (received = value));

    httpMock
      .expectOne(`${base}/legacy`)
      .flush(envelope({ username: 'legacy', usertype: 'Participant' }));
    tick();

    expect(received?.UsernameDisplay).toBe('legacy');
    expect(received?.CreatedAtUtc).toBe('');
  }));

  /// A payload missing a field the interface declares as required is a broken contract, not
  /// something to paper over with a half-built object.
  it('errors when a public profile payload is missing its username', fakeAsync(() => {
    let thrown: Error | undefined;
    service.getPublicProfile('ghost').subscribe({ error: (err: Error) => (thrown = err) });

    httpMock.expectOne(`${base}/ghost`).flush(envelope({ usertype: 'Participant' }));
    tick();

    expect(thrown?.message).toBe('Profile response was incomplete.');
  }));

  it('errors when the profile payload is not an object at all', fakeAsync(() => {
    let thrown: Error | undefined;
    service.getMyProfile().subscribe({ error: (err: Error) => (thrown = err) });
    tick();

    httpMock.expectOne(base).flush(envelope('not-a-profile'));
    tick();

    expect(thrown?.message).toBe('Profile response was incomplete.');
  }));

  /// Booleans and timestamps a partial payload omits must land as usable defaults rather than
  /// undefined, since the account page branches on them.
  it('defaults the flags and timestamps a partial profile payload omits', fakeAsync(() => {
    let received: MyProfile | undefined;
    service.getMyProfile().subscribe((value) => (received = value));
    tick();

    httpMock.expectOne(base).flush(
      envelope({
        id: 3,
        email: 'partial@example.com',
        username: 'partial',
        usertype: 'Participant',
      }),
    );
    tick();

    expect(received?.CanChangeUsername).toBeFalse();
    expect(received?.HasLocalPassword).toBeFalse();
    expect(received?.GoogleLinked).toBeFalse();
    expect(received?.MicrosoftLinked).toBeFalse();
    expect(received?.CreatedAtUtc).toBe('');
    expect(received?.UpdatedAtUtc).toBe('');
    expect(received?.UsernameDisplay).toBe('partial');
  }));

  it('errors when the profile payload is missing a required field', fakeAsync(() => {
    let thrown: Error | undefined;
    service.getMyProfile().subscribe({ error: (err: Error) => (thrown = err) });
    tick();

    httpMock.expectOne(base).flush(envelope({ id: 1, email: 'x@example.com' }));
    tick();

    expect(thrown?.message).toBe('Profile response was incomplete.');
  }));

  /// The same defect class the profile normalisers fixed, in the same file: a raw cast against a
  /// camelCase payload left challenge.Challenge undefined and broke the email-change OTP handoff.
  it('reads a camelCase email-change challenge', fakeAsync(() => {
    let received: { Challenge: string; ExpiresAtUtc: string } | undefined;
    service.requestEmailChange('next@example.com').subscribe((value) => (received = value));
    tick();

    httpMock
      .expectOne(`${base}/email`)
      .flush(envelope({ challenge: 'challenge-token', expiresAtUtc: '2026-02-01T00:00:00Z' }));
    tick();

    expect(received?.Challenge).toBe('challenge-token');
    expect(received?.ExpiresAtUtc).toBe('2026-02-01T00:00:00Z');
  }));

  it('reads a camelCase pending email change', fakeAsync(() => {
    let received: { NewEmail: string } | null | undefined;
    service.getPendingEmailChange().subscribe((value) => (received = value));
    tick();

    httpMock
      .expectOne(`${base}/email/pending`)
      .flush(envelope({ newEmail: 'next@example.com', expiresAtUtc: '2026-02-01T00:00:00Z' }));
    tick();

    expect(received?.NewEmail).toBe('next@example.com');
  }));

  /// Nothing awaiting confirmation is a normal answer, not an error.
  it('resolves a pending email change to null when there is none', fakeAsync(() => {
    let received: unknown = 'unset';
    service.getPendingEmailChange().subscribe((value) => (received = value));
    tick();

    httpMock.expectOne(`${base}/email/pending`).flush(envelope(null));
    tick();

    expect(received).toBeNull();
  }));
});
