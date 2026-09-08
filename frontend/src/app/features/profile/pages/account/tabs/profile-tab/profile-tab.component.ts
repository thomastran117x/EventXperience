import { CommonModule } from '@angular/common';
import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Store } from '@ngrx/store';
import { finalize } from 'rxjs/operators';

import {
  getApiClientMessage,
  isApiClientErrorCode,
} from '../../../../../../core/api/models/api-client-error.model';
import { AuthTokenService } from '../../../../../../core/api/services/auth-token.service';
import { setUser } from '../../../../../../core/stores/user.actions';
import { User } from '../../../../../../core/stores/user.model';
import { AuthService } from '../../../../../auth/services/auth.service';
import { emailAvailabilityValidator } from '../../../../../auth/validators/email-availability.validator';
import {
  MyProfile,
  PendingEmailChange,
  ProfileService,
} from '../../../../services/profile.service';
import { MfaGateComponent } from '../../mfa-gate/mfa-gate.component';

const MAX_AVATAR_BYTES = 5 * 1024 * 1024;
const MFA_REQUIRED_ERROR_CODE = 'MFA_REQUIRED';

@Component({
  selector: 'app-profile-tab',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, RouterLink, MfaGateComponent],
  templateUrl: './profile-tab.component.html',
})
export class ProfileTabComponent implements OnInit {
  private readonly fb = new FormBuilder();
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);
  private readonly authToken = inject(AuthTokenService);
  private readonly router = inject(Router);

  readonly profileForm = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control('', [Validators.maxLength(100)]),
    phone: this.fb.nonNullable.control('', [Validators.maxLength(30)]),
    address: this.fb.nonNullable.control('', [Validators.maxLength(200)]),
  });

  readonly usernameForm = this.fb.nonNullable.group({
    username: this.fb.nonNullable.control('', [Validators.required, Validators.maxLength(50)]),
  });

  readonly emailForm = this.fb.nonNullable.group({
    newEmail: this.fb.nonNullable.control(
      '',
      [Validators.required, Validators.email, Validators.maxLength(254)],
      // The same debounced probe the signup form uses. It fails open, so a probe outage never
      // blocks the form - the server re-checks authoritatively at request and confirm time.
      [emailAvailabilityValidator(this.auth)],
    ),
    currentPassword: this.fb.nonNullable.control(''),
  });

  readonly emailCodeForm = this.fb.nonNullable.group({
    code: this.fb.nonNullable.control('', [Validators.required, Validators.pattern(/^\d{6}$/)]),
  });

  profile: MyProfile | null = null;
  loading = true;
  editing = false;
  saving = false;
  avatarUploading = false;
  usernameChangeRequested = false;
  usernameMfaVerified = false;
  usernameSaving = false;
  emailChangeRequested = false;
  emailMfaVerified = false;
  emailSaving = false;
  emailChallenge = '';
  pendingEmailChange: PendingEmailChange | null = null;
  error = '';
  success = '';

  constructor(
    private store: Store,
    private profileService: ProfileService,
  ) {}

  ngOnInit(): void {
    this.loadProfile();
    this.loadPendingEmailChange();
  }

  get userInitials(): string {
    const name = this.profile?.Name || this.profile?.Username || '';
    return name ? name.slice(0, 2).toUpperCase() : '?';
  }

  /** True once a confirmation has been sent and is still waiting to be entered. */
  get awaitingEmailConfirmation(): boolean {
    return this.pendingEmailChange !== null;
  }

  get requiresPasswordForEmailChange(): boolean {
    // An OAuth-only account has no password to prove; MFA step-up is the whole gate.
    return !!this.profile && !this.profile.GoogleLinked && !this.profile.MicrosoftLinked;
  }

  get usertypeLabel(): string {
    const type = this.profile?.Usertype ?? '';
    return type.charAt(0).toUpperCase() + type.slice(1);
  }

  private loadProfile(): void {
    this.loading = true;
    this.profileService
      .getMyProfile()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.loading = false)),
      )
      .subscribe({
        next: (profile) => {
          this.profile = profile;
          this.resetForm();
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to load your profile.');
        },
      });
  }

  private resetForm(): void {
    if (!this.profile) return;
    this.profileForm.patchValue({
      name: this.profile.Name ?? '',
      phone: this.profile.Phone ?? '',
      address: this.profile.Address ?? '',
    });
    this.usernameForm.setValue({ username: this.profile.Username });
  }

  startEditing(): void {
    this.editing = true;
    this.error = '';
    this.success = '';
  }

  cancelEditing(): void {
    this.editing = false;
    this.error = '';
    this.resetForm();
  }

  saveProfile(): void {
    if (this.profileForm.invalid) {
      this.profileForm.markAllAsTouched();
      return;
    }

    const { name, phone, address } = this.profileForm.getRawValue();
    this.saving = true;
    this.error = '';
    this.success = '';

    this.profileService
      .updateProfile({
        name: name || undefined,
        phone: phone || undefined,
        address: address || undefined,
      })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.saving = false)),
      )
      .subscribe({
        next: (updated) => {
          this.profile = updated;
          this.syncStore(updated);
          this.editing = false;
          this.success = 'Profile updated successfully.';
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to update profile.');
        },
      });
  }

  startUsernameChange(): void {
    if (!this.profile?.CanChangeUsername) return;
    this.usernameChangeRequested = true;
    this.usernameMfaVerified = false;
    this.usernameForm.setValue({ username: this.profile.Username });
    this.error = '';
    this.success = '';
  }

  cancelUsernameChange(): void {
    this.usernameChangeRequested = false;
    this.usernameMfaVerified = false;
    this.error = '';
    if (this.profile) {
      this.usernameForm.setValue({ username: this.profile.Username });
    }
  }

  changeUsername(): void {
    if (!this.usernameMfaVerified) {
      return;
    }

    const username = this.usernameForm.getRawValue().username.trim().toLowerCase();
    this.usernameForm.controls.username.setValue(username);
    if (this.usernameForm.invalid) {
      this.usernameForm.markAllAsTouched();
      return;
    }

    this.usernameSaving = true;
    this.error = '';
    this.success = '';

    this.profileService
      .changeUsername(username)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.usernameSaving = false)),
      )
      .subscribe({
        next: (updated) => {
          this.profile = updated;
          this.syncStore(updated);
          this.usernameChangeRequested = false;
          this.usernameMfaVerified = false;
          this.usernameForm.setValue({ username: updated.Username });
          this.success = `Username changed to @${updated.Username}.`;
        },
        error: (err) => {
          if (isApiClientErrorCode(err, MFA_REQUIRED_ERROR_CODE)) {
            this.usernameMfaVerified = false;
          }
          this.error = getApiClientMessage(err, 'Unable to change username.');
        },
      });
  }

  private loadPendingEmailChange(): void {
    this.profileService
      .getPendingEmailChange()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (pending) => (this.pendingEmailChange = pending),
        // A pending change is supplementary information; failing to read it must not break the
        // page, and the user can always start a fresh request.
        error: () => (this.pendingEmailChange = null),
      });
  }

  startEmailChange(): void {
    this.emailChangeRequested = true;
    this.emailMfaVerified = false;
    this.emailForm.reset({ newEmail: '', currentPassword: '' });
    this.error = '';
    this.success = '';
  }

  cancelEmailChange(): void {
    this.emailChangeRequested = false;
    this.emailMfaVerified = false;
    this.emailForm.reset({ newEmail: '', currentPassword: '' });
    this.error = '';
  }

  requestEmailChange(): void {
    if (!this.emailMfaVerified) return;

    if (this.emailForm.invalid) {
      this.emailForm.markAllAsTouched();
      return;
    }

    const { newEmail, currentPassword } = this.emailForm.getRawValue();
    this.emailSaving = true;
    this.error = '';
    this.success = '';

    this.profileService
      .requestEmailChange(
        newEmail.trim(),
        this.requiresPasswordForEmailChange ? currentPassword : undefined,
      )
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.emailSaving = false)),
      )
      .subscribe({
        next: (challenge) => {
          this.emailChallenge = challenge.Challenge;
          this.pendingEmailChange = {
            NewEmail: newEmail.trim(),
            ExpiresAtUtc: challenge.ExpiresAtUtc,
          };
          this.emailChangeRequested = false;
          this.emailCodeForm.reset({ code: '' });
          this.success = `We sent a confirmation code to ${newEmail.trim()}.`;
        },
        error: (err) => {
          if (isApiClientErrorCode(err, MFA_REQUIRED_ERROR_CODE)) {
            this.emailMfaVerified = false;
          }
          this.error = getApiClientMessage(err, 'Unable to start the email change.');
        },
      });
  }

  confirmEmailChange(): void {
    if (this.emailCodeForm.invalid || !this.emailChallenge) {
      this.emailCodeForm.markAllAsTouched();
      return;
    }

    const { code } = this.emailCodeForm.getRawValue();
    this.emailSaving = true;
    this.error = '';
    this.success = '';

    this.auth
      .confirmEmailChange({ code, challenge: this.emailChallenge })
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.emailSaving = false)),
      )
      .subscribe({
        // Confirming revokes every session server-side, so the only correct client state is
        // signed out. Same shape as the password tab.
        next: () => {
          this.authToken.logoutLocal();
          void this.router.navigate(['/auth/login']);
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to confirm the email change.');
        },
      });
  }

  abandonEmailChange(): void {
    this.emailSaving = true;
    this.error = '';
    this.success = '';

    this.profileService
      .cancelEmailChange()
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.emailSaving = false)),
      )
      .subscribe({
        next: () => {
          this.pendingEmailChange = null;
          this.emailChallenge = '';
          this.success = 'Email change cancelled.';
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to cancel the email change.');
        },
      });
  }

  onAvatarSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.error = '';
    this.success = '';

    if (!file.type.startsWith('image/')) {
      this.error = 'Please choose an image file.';
      return;
    }
    if (file.size > MAX_AVATAR_BYTES) {
      this.error = 'Image must be smaller than 5MB.';
      return;
    }

    this.avatarUploading = true;
    this.profileService
      .uploadAvatar(file)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => (this.avatarUploading = false)),
      )
      .subscribe({
        next: (updated) => {
          this.profile = updated;
          this.syncStore(updated);
          this.success = 'Profile photo updated.';
        },
        error: (err) => {
          this.error = getApiClientMessage(err, 'Unable to upload profile photo.');
        },
      });
  }

  private syncStore(profile: MyProfile): void {
    const user: User = {
      Id: profile.Id,
      Email: profile.Email,
      Username: profile.Username,
      Name: profile.Name ?? null,
      Avatar: profile.Avatar ?? null,
      Usertype: profile.Usertype,
    };
    this.store.dispatch(setUser({ user }));
  }
}
