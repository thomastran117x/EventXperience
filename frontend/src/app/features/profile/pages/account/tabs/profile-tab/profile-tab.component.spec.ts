import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { provideTestStore } from '@testing';

import { ApiClientClientError } from '../../../../../../core/api/models/api-client-error.model';
import { AuthTokenService } from '../../../../../../core/api/services/auth-token.service';
import { AuthService } from '../../../../../auth/services/auth.service';
import { MyProfile, ProfileService } from '../../../../services/profile.service';
import { ProfileTabComponent } from './profile-tab.component';

type AuthStub = Pick<AuthService, 'confirmEmailChange' | 'checkEmailAvailability'>;
type AuthTokenStub = Pick<AuthTokenService, 'logoutLocal'>;

function makeProfile(overrides: Partial<MyProfile> = {}): MyProfile {
  return {
    Id: 7,
    Email: 'member@example.com',
    Username: 'member',
    CanChangeUsername: true,
    UsernameChangeAvailableAtUtc: null,
    Name: 'Member',
    Avatar: null,
    Usertype: 'Participant',
    Phone: null,
    Address: null,
    HasLocalPassword: true,
    GoogleLinked: false,
    MicrosoftLinked: false,
    CreatedAtUtc: '2026-01-01T00:00:00Z',
    UpdatedAtUtc: '2026-01-02T00:00:00Z',
    ...overrides,
  };
}

describe('ProfileTabComponent', () => {
  let fixture: ComponentFixture<ProfileTabComponent>;
  let component: ProfileTabComponent;
  let profileService: jasmine.SpyObj<ProfileService>;
  let auth: jasmine.SpyObj<AuthStub>;
  let authToken: jasmine.SpyObj<AuthTokenStub>;
  let router: Router;

  beforeEach(async () => {
    profileService = jasmine.createSpyObj<ProfileService>('ProfileService', [
      'getMyProfile',
      'updateProfile',
      'changeUsername',
      'uploadAvatar',
      'requestEmailChange',
      'getPendingEmailChange',
      'cancelEmailChange',
    ]);
    profileService.getMyProfile.and.returnValue(of(makeProfile()));
    profileService.getPendingEmailChange.and.returnValue(of(null));

    auth = jasmine.createSpyObj<AuthStub>('AuthService', [
      'confirmEmailChange',
      'checkEmailAvailability',
    ]);
    auth.checkEmailAvailability.and.returnValue(of({ email: '', available: true }));

    authToken = jasmine.createSpyObj<AuthTokenStub>('AuthTokenService', ['logoutLocal']);

    await TestBed.configureTestingModule({
      imports: [ProfileTabComponent],
      providers: [
        { provide: ProfileService, useValue: profileService },
        { provide: AuthService, useValue: auth },
        { provide: AuthTokenService, useValue: authToken },
        provideRouter([]),
        ...provideTestStore(),
      ],
    }).compileComponents();

    router = TestBed.inject(Router);

    fixture = TestBed.createComponent(ProfileTabComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('keeps ordinary profile updates separate from username changes', () => {
    profileService.updateProfile.and.returnValue(of(makeProfile({ Name: 'New Name' })));
    component.startEditing();
    component.profileForm.patchValue({ name: 'New Name', phone: '555-1111' });

    component.saveProfile();

    expect(profileService.updateProfile).toHaveBeenCalledWith({
      name: 'New Name',
      phone: '555-1111',
      address: undefined,
    });
    expect(profileService.changeUsername).not.toHaveBeenCalled();
  });

  it('does not submit an invalid ordinary profile form', () => {
    component.profileForm.controls.name.setValue('x'.repeat(101));

    component.saveProfile();

    expect(component.profileForm.controls.name.touched).toBeTrue();
    expect(profileService.updateProfile).not.toHaveBeenCalled();
  });

  it('surfaces ordinary profile update failures', () => {
    profileService.updateProfile.and.returnValue(
      throwError(() => new ApiClientClientError('Update failed', 409, 'PROFILE_CONFLICT')),
    );

    component.saveProfile();

    expect(component.error).toBe('Update failed');
    expect(component.saving).toBeFalse();
  });

  it('normalizes a verified username change and applies the cooldown response', () => {
    const changed = makeProfile({
      Username: 'new-name',
      CanChangeUsername: false,
      UsernameChangeAvailableAtUtc: '2026-09-14T12:00:00Z',
    });
    profileService.changeUsername.and.returnValue(of(changed));
    component.startUsernameChange();
    component.usernameMfaVerified = true;
    component.usernameForm.setValue({ username: '  NEW-NAME  ' });

    component.changeUsername();

    expect(profileService.changeUsername).toHaveBeenCalledOnceWith('new-name');
    expect(component.profile).toEqual(changed);
    expect(component.usernameChangeRequested).toBeFalse();
    expect(component.success).toContain('@new-name');
  });

  it('does not open the rename flow while cooldown is active', () => {
    component.profile = makeProfile({
      CanChangeUsername: false,
      UsernameChangeAvailableAtUtc: '2026-09-14T12:00:00Z',
    });

    component.startUsernameChange();

    expect(component.usernameChangeRequested).toBeFalse();
  });

  it('does not submit a username until MFA verification completes', () => {
    component.startUsernameChange();
    component.usernameForm.setValue({ username: 'next-name' });

    component.changeUsername();

    expect(profileService.changeUsername).not.toHaveBeenCalled();
  });

  it('normalizes before rejecting an empty username', () => {
    component.startUsernameChange();
    component.usernameMfaVerified = true;
    component.usernameForm.setValue({ username: '   ' });

    component.changeUsername();

    expect(component.usernameForm.controls.username.value).toBe('');
    expect(component.usernameForm.controls.username.hasError('required')).toBeTrue();
    expect(profileService.changeUsername).not.toHaveBeenCalled();
  });

  it('restores the current username when the rename flow is cancelled', () => {
    component.startUsernameChange();
    component.usernameMfaVerified = true;
    component.usernameForm.setValue({ username: 'abandoned-name' });

    component.cancelUsernameChange();

    expect(component.usernameChangeRequested).toBeFalse();
    expect(component.usernameMfaVerified).toBeFalse();
    expect(component.usernameForm.controls.username.value).toBe('member');
  });

  it('returns to the MFA gate when the server says step-up verification expired', () => {
    profileService.changeUsername.and.returnValue(
      throwError(() => new ApiClientClientError('Verify first', 403, 'MFA_REQUIRED')),
    );
    component.startUsernameChange();
    component.usernameMfaVerified = true;
    component.usernameForm.setValue({ username: 'next-name' });

    component.changeUsername();

    expect(component.usernameMfaVerified).toBeFalse();
    expect(component.error).toBe('Verify first');
  });

  it('keeps MFA verification for a non-MFA username API failure', () => {
    profileService.changeUsername.and.returnValue(
      throwError(() => new ApiClientClientError('Already taken', 409, 'USERNAME_TAKEN')),
    );
    component.startUsernameChange();
    component.usernameMfaVerified = true;
    component.usernameForm.setValue({ username: 'claimed-name' });

    component.changeUsername();

    expect(component.usernameMfaVerified).toBeTrue();
    expect(component.error).toBe('Already taken');
    expect(component.usernameSaving).toBeFalse();
  });

  it('labels the cooldown availability date as UTC', () => {
    component.profile = makeProfile({
      CanChangeUsername: false,
      UsernameChangeAvailableAtUtc: '2026-09-14T00:00:00Z',
    });

    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('September 14, 2026 UTC');
  });

  it('derives initials from the username and handles an unloaded profile', () => {
    component.profile = makeProfile({ Name: null, Username: 'username' });
    expect(component.userInitials).toBe('US');

    component.profile = null;
    expect(component.userInitials).toBe('?');
    expect(component.usertypeLabel).toBe('');
  });

  it('ignores an avatar event without a file', () => {
    const input = { files: [], value: 'selected' };

    component.onAvatarSelected({ target: input } as unknown as Event);

    expect(input.value).toBe('');
    expect(profileService.uploadAvatar).not.toHaveBeenCalled();
  });

  it('rejects non-image and oversized avatar files locally', () => {
    const nonImage = new File(['not-an-image'], 'avatar.txt', { type: 'text/plain' });
    component.onAvatarSelected({
      target: { files: [nonImage], value: 'selected' },
    } as unknown as Event);
    expect(component.error).toBe('Please choose an image file.');

    const oversized = new File([new Uint8Array(5 * 1024 * 1024 + 1)], 'avatar.png', {
      type: 'image/png',
    });
    component.onAvatarSelected({
      target: { files: [oversized], value: 'selected' },
    } as unknown as Event);

    expect(component.error).toBe('Image must be smaller than 5MB.');
    expect(profileService.uploadAvatar).not.toHaveBeenCalled();
  });

  it('updates the profile after a valid avatar upload', () => {
    const updated = makeProfile({ Avatar: '/avatars/new.png' });
    profileService.uploadAvatar.and.returnValue(of(updated));
    const image = new File(['image'], 'avatar.png', { type: 'image/png' });

    component.onAvatarSelected({
      target: { files: [image], value: 'selected' },
    } as unknown as Event);

    expect(profileService.uploadAvatar).toHaveBeenCalledOnceWith(image);
    expect(component.profile).toEqual(updated);
    expect(component.success).toBe('Profile photo updated.');
    expect(component.avatarUploading).toBeFalse();
  });

  describe('email change', () => {
    const challenge = { Challenge: 'challenge-token', ExpiresAtUtc: '2026-09-07T12:30:00Z' };

    it('does not send a request until step-up verification has passed', () => {
      component.startEmailChange();
      component.emailForm.patchValue({
        newEmail: 'new@example.com',
        currentPassword: 'Password123!',
      });

      component.requestEmailChange();

      expect(profileService.requestEmailChange).not.toHaveBeenCalled();
    });

    it('moves to the confirmation step once the code has been sent', () => {
      profileService.requestEmailChange.and.returnValue(of(challenge));
      component.startEmailChange();
      component.emailMfaVerified = true;
      component.emailForm.patchValue({
        newEmail: 'new@example.com',
        currentPassword: 'Password123!',
      });

      component.requestEmailChange();

      expect(profileService.requestEmailChange).toHaveBeenCalledWith(
        'new@example.com',
        'Password123!',
      );
      expect(component.awaitingEmailConfirmation).toBeTrue();
      expect(component.pendingEmailChange?.NewEmail).toBe('new@example.com');
      expect(component.emailChallenge).toBe('challenge-token');
    });

    // An OAuth-only account has no password to prove.
    it('omits the password when the account has none', () => {
      profileService.getMyProfile.and.returnValue(
        of(makeProfile({ HasLocalPassword: false, GoogleLinked: true })),
      );
      fixture = TestBed.createComponent(ProfileTabComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      profileService.requestEmailChange.and.returnValue(of(challenge));
      component.startEmailChange();
      component.emailMfaVerified = true;
      component.emailForm.patchValue({ newEmail: 'new@example.com' });

      component.requestEmailChange();

      expect(profileService.requestEmailChange).toHaveBeenCalledWith('new@example.com', undefined);
    });

    it('still asks for the password when a provider is linked to a password account', () => {
      profileService.getMyProfile.and.returnValue(
        of(makeProfile({ HasLocalPassword: true, GoogleLinked: true })),
      );
      const linked = TestBed.createComponent(ProfileTabComponent);
      linked.detectChanges();
      const instance = linked.componentInstance;

      profileService.requestEmailChange.and.returnValue(of(challenge));
      instance.startEmailChange();
      instance.emailMfaVerified = true;
      instance.emailForm.patchValue({
        newEmail: 'new@example.com',
        currentPassword: 'Password123!',
      });

      instance.requestEmailChange();

      expect(instance.requiresPasswordForEmailChange).toBeTrue();
      expect(profileService.requestEmailChange).toHaveBeenCalledWith(
        'new@example.com',
        'Password123!',
      );
    });

    it('returns to the step-up gate when the server says verification expired', () => {
      profileService.requestEmailChange.and.returnValue(
        throwError(() => new ApiClientClientError('Step-up required', 403, 'MFA_REQUIRED')),
      );
      component.startEmailChange();
      component.emailMfaVerified = true;
      component.emailForm.patchValue({
        newEmail: 'new@example.com',
        currentPassword: 'Password123!',
      });

      component.requestEmailChange();

      expect(component.emailMfaVerified).toBeFalse();
      expect(component.error).toContain('Step-up required');
    });

    it('surfaces a conflict without moving to the confirmation step', () => {
      profileService.requestEmailChange.and.returnValue(
        throwError(
          () => new ApiClientClientError('That email is already in use.', 409, 'CONFLICT'),
        ),
      );
      component.startEmailChange();
      component.emailMfaVerified = true;
      component.emailForm.patchValue({
        newEmail: 'taken@example.com',
        currentPassword: 'Password123!',
      });

      component.requestEmailChange();

      expect(component.awaitingEmailConfirmation).toBeFalse();
      expect(component.error).toContain('already in use');
    });

    // Confirming revokes every session server-side, so staying signed in locally would leave the
    // page holding a token the API has already rejected.
    it('signs out and returns to login once the change is confirmed', () => {
      auth.confirmEmailChange.and.returnValue(of(undefined));
      const navigate = spyOn(router, 'navigate').and.resolveTo(true);
      component.pendingEmailChange = {
        NewEmail: 'new@example.com',
        ExpiresAtUtc: challenge.ExpiresAtUtc,
      };
      component.emailChallenge = 'challenge-token';
      component.emailCodeForm.setValue({ code: '123456' });

      component.confirmEmailChange();

      expect(auth.confirmEmailChange).toHaveBeenCalledWith({
        code: '123456',
        challenge: 'challenge-token',
      });
      expect(authToken.logoutLocal).toHaveBeenCalled();
      expect(navigate).toHaveBeenCalledWith(['/auth/login']);
    });

    it('rejects a code that is not six digits', () => {
      component.emailChallenge = 'challenge-token';
      component.emailCodeForm.setValue({ code: '12ab' });

      component.confirmEmailChange();

      expect(auth.confirmEmailChange).not.toHaveBeenCalled();
    });

    it('keeps the user signed in when confirmation fails', () => {
      auth.confirmEmailChange.and.returnValue(
        throwError(() => new ApiClientClientError('Invalid code.', 401, 'UNAUTHORIZED')),
      );
      component.emailChallenge = 'challenge-token';
      component.emailCodeForm.setValue({ code: '123456' });

      component.confirmEmailChange();

      expect(authToken.logoutLocal).not.toHaveBeenCalled();
      expect(component.error).toContain('Invalid code.');
    });

    it('clears the pending change when it is cancelled', () => {
      profileService.cancelEmailChange.and.returnValue(of(undefined));
      component.pendingEmailChange = {
        NewEmail: 'new@example.com',
        ExpiresAtUtc: challenge.ExpiresAtUtc,
      };
      component.emailChallenge = 'challenge-token';

      component.abandonEmailChange();

      expect(component.awaitingEmailConfirmation).toBeFalse();
      expect(component.emailChallenge).toBe('');
    });

    it('reports a failed cancellation without dropping the pending state', () => {
      profileService.cancelEmailChange.and.returnValue(
        throwError(() => new ApiClientClientError('Nope', 500, 'SERVER')),
      );
      component.pendingEmailChange = {
        NewEmail: 'new@example.com',
        ExpiresAtUtc: challenge.ExpiresAtUtc,
      };

      component.abandonEmailChange();

      expect(component.awaitingEmailConfirmation).toBeTrue();
      expect(component.error).toBeTruthy();
    });

    // The emailed link is still a way through, so a failed read must not hide the card.
    it('renders normally when the pending change cannot be read', () => {
      profileService.getPendingEmailChange.and.returnValue(
        throwError(() => new ApiClientClientError('Nope', 500, 'SERVER')),
      );

      const retry = TestBed.createComponent(ProfileTabComponent);
      retry.detectChanges();

      expect(retry.componentInstance.pendingEmailChange).toBeNull();
    });

    it('restores the confirmation step for a change already in flight', () => {
      profileService.getPendingEmailChange.and.returnValue(
        of({ NewEmail: 'new@example.com', ExpiresAtUtc: challenge.ExpiresAtUtc }),
      );

      const restored = TestBed.createComponent(ProfileTabComponent);
      restored.detectChanges();

      expect(restored.componentInstance.awaitingEmailConfirmation).toBeTrue();
      // The challenge only lived in memory, so the page falls back to the emailed link.
      expect(restored.componentInstance.emailChallenge).toBe('');
    });

    it('drops the form when the change is abandoned before sending', () => {
      component.startEmailChange();
      component.emailMfaVerified = true;
      component.emailForm.patchValue({ newEmail: 'new@example.com' });

      component.cancelEmailChange();

      expect(component.emailMfaVerified).toBeFalse();
      expect(component.emailForm.controls.newEmail.value).toBe('');
    });
  });
});
