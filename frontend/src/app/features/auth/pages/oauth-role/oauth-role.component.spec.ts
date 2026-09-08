import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ApiClientClientError } from '../../../../core/api/models/api-client-error.model';
import { AuthenticatedSessionResponse } from '../../../../core/models/auth-response.model';
import { SessionManagerService } from '../../../../core/services/session-manager.service';
import { AuthService, PendingOAuthSignupStorageKey } from '../../services/auth.service';
import { AuthReturnUrlService } from '../../services/auth-return-url.service';
import { OAuthRoleComponent } from './oauth-role.component';

function makeSession(): AuthenticatedSessionResponse {
  return {
    AccessToken: 'access-token',
    ExpiresAtUtc: '2099-01-01T00:00:00Z',
    ReturnPath: '/dashboard',
  } as AuthenticatedSessionResponse;
}

describe('OAuthRoleComponent', () => {
  let fixture: ComponentFixture<OAuthRoleComponent>;
  let component: OAuthRoleComponent;
  let auth: jasmine.SpyObj<AuthService>;
  let sessionManager: jasmine.SpyObj<SessionManagerService>;

  function stashPending(email = 'Ada.Lovelace@example.com'): void {
    sessionStorage.setItem(
      PendingOAuthSignupStorageKey,
      JSON.stringify({
        SignupToken: 'signup-token',
        Email: email,
        Name: 'Ada Lovelace',
        Provider: 'google',
      }),
    );
  }

  beforeEach(async () => {
    sessionStorage.clear();

    auth = jasmine.createSpyObj<AuthService>('AuthService', [
      'completeOAuthSignup',
      'checkUsernameAvailability',
    ]);
    auth.checkUsernameAvailability.and.returnValue(of({ username: '', available: true }));
    auth.completeOAuthSignup.and.returnValue(of(makeSession()));

    sessionManager = jasmine.createSpyObj<SessionManagerService>('SessionManagerService', [
      'bootstrapSession',
    ]);
    sessionManager.bootstrapSession.and.returnValue(Promise.resolve());

    await TestBed.configureTestingModule({
      imports: [OAuthRoleComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: SessionManagerService, useValue: sessionManager },
        {
          provide: AuthReturnUrlService,
          useValue: jasmine.createSpyObj<AuthReturnUrlService>('AuthReturnUrlService', ['consume']),
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(OAuthRoleComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => sessionStorage.clear());

  it('prefills a username derived from the provider email', fakeAsync(() => {
    stashPending();

    fixture.detectChanges();
    tick(400);

    expect(component.form.controls.username.value).toBe('ada.lovelace');
    expect(auth.checkUsernameAvailability).toHaveBeenCalledWith('ada.lovelace');
  }));

  it('leaves the field empty when no usable name can be derived from the email', fakeAsync(() => {
    stashPending('ab@example.com');

    fixture.detectChanges();
    tick(400);

    expect(component.form.controls.username.value).toBe('');
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();
  }));

  it('sends the normalized username with the role', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    component.form.controls.username.setValue('  Ada_Lovelace  ');
    component.form.controls.usertype.setValue('organizer');
    tick(400);

    component.submit();
    tick();

    expect(auth.completeOAuthSignup).toHaveBeenCalledWith(
      'signup-token',
      'organizer',
      'ada_lovelace',
    );
  }));

  it('does not submit while the username is malformed', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    component.form.controls.username.setValue('a..b');
    tick(400);

    component.submit();
    tick();

    expect(auth.completeOAuthSignup).not.toHaveBeenCalled();
    expect(component.usernameFormatMessage()).toContain('must start and end with');
  }));

  it('reports a username the API says is taken', fakeAsync(() => {
    auth.checkUsernameAvailability.and.returnValue(
      of({ username: 'ada.lovelace', available: false }),
    );
    stashPending();

    fixture.detectChanges();
    tick(400);
    fixture.detectChanges();

    expect(component.form.controls.username.hasError('usernameTaken')).toBeTrue();
    expect(component.usernameAvailable()).toBeFalse();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'That username is already taken.',
    );
  }));

  // The server deletes the pending-signup key only once the account exists, so a conflict leaves
  // the token usable and the user can retry here with a different name.
  it('keeps the form usable after the server reports the name was taken', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    tick(400);
    auth.completeOAuthSignup.and.returnValue(
      throwError(() => new ApiClientClientError('Already taken', 409, 'USERNAME_TAKEN')),
    );

    component.submit();
    tick();

    expect(component.status()).toBe('error');
    expect(component.message()).toBe('Already taken');
    expect(component.pending()).not.toBeNull();
  }));

  it('reports a missing signup session when nothing was stashed', () => {
    fixture.detectChanges();

    expect(component.status()).toBe('error');
    expect(component.message()).toContain('was not found');
    expect(component.pending()).toBeNull();
  });

  it('reports an invalid signup session and clears it', () => {
    sessionStorage.setItem(PendingOAuthSignupStorageKey, JSON.stringify({ Email: 'ada@x.com' }));

    fixture.detectChanges();

    expect(component.status()).toBe('error');
    expect(component.message()).toContain('invalid');
    expect(sessionStorage.getItem(PendingOAuthSignupStorageKey)).toBeNull();
  });

  it('refuses to submit when the pending session vanished after the form was filled', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    tick(400);
    component.pending.set(null);

    component.submit();
    tick();

    expect(auth.completeOAuthSignup).not.toHaveBeenCalled();
    expect(component.message()).toContain('missing');
  }));

  it('reports no format message while the username is acceptable', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    tick(400);

    expect(component.usernameFormatMessage()).toBeNull();
    expect(component.usernameAvailable()).toBeTrue();
  }));

  it('surfaces a failure to bootstrap the new session', fakeAsync(() => {
    sessionManager.bootstrapSession.and.callFake(() => Promise.reject(new Error('no session')));
    stashPending();
    fixture.detectChanges();
    tick(400);

    component.submit();
    tick();

    expect(component.status()).toBe('error');
  }));

  it('bootstraps the session once signup completes', fakeAsync(() => {
    stashPending();
    fixture.detectChanges();
    tick(400);

    component.submit();
    tick();

    expect(sessionManager.bootstrapSession).toHaveBeenCalled();
    expect(sessionStorage.getItem(PendingOAuthSignupStorageKey)).toBeNull();
  }));
});
