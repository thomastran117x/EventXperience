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
import { AuthService, UsernameSuggestion } from '../../../../../auth/services/auth.service';
import { emailAvailabilityValidator } from '../../../../../auth/validators/email-availability.validator';
import {
  normalizeUsername,
  usernameAvailabilityValidator,
} from '../../../../../auth/validators/username-availability.validator';
import {
  toUsernameDisplay,
  usernameFormatValidator,
} from '../../../../../auth/validators/username-format.validator';
import {
  MyProfile,
  PendingEmailChange,
  ProfileService,
} from '../../../../services/profile.service';
import { MfaGateComponent } from '../../mfa-gate/mfa-gate.component';

const MAX_AVATAR_BYTES = 5 * 1024 * 1024;
const MFA_REQUIRED_ERROR_CODE = 'MFA_REQUIRED';

import { UsernameSuggestionsComponent } from '../../../../../auth/components/username-suggestions/username-suggestions.component';

@Component({
  selector: 'app-profile-tab',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterLink,
    MfaGateComponent,
    UsernameSuggestionsComponent,
  ],
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
    username: this.fb.nonNullable.control(
      '',
      [Validators.required, usernameFormatValidator],
      // The same debounced probe the signup form uses, exempting the name the account already
      // holds: the API would report that one taken, and resetForm() prefills it on every load.
      [
        usernameAvailabilityValidator(
          this.auth,
          (username) => (this.confirmedUsernameAvailable = username),
          { exempt: () => this.profile?.Username ?? null },
        ),
      ],
    ),
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

  /** The last username the API actually confirmed as free; null whenever we did not get an answer. */
  private confirmedUsernameAvailable: string | null = null;

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
    // Driven by whether a password exists, not by whether a provider is linked: an account can
    // have both, and the API asks for the password whenever it has one to check.
    return !!this.profile && this.profile.HasLocalPassword;
  }

  get usernameChecking(): boolean {
    return this.usernameForm.controls.username.pending;
  }

  /**
   * Only claim availability for a name the API actually confirmed. The validator fails open, so a
   * failed or rate-limited probe also leaves the control VALID - reading validity alone would
   * announce "available" when nothing was ever checked.
   */
  get usernameAvailable(): boolean {
    const control = this.usernameForm.controls.username;
    return (
      control.valid &&
      this.confirmedUsernameAvailable !== null &&
      this.confirmedUsernameAvailable === normalizeUsername(control.value)
    );
  }

  /** The server's own wording for why this value would be rejected, or null when it is fine. */
  get usernameFormatMessage(): string | null {
    return this.usernameForm.controls.username.errors?.['usernameFormat']?.message ?? null;
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

  suggestions: UsernameSuggestion[] = [];
  suggestionsLoading = false;

  /**
   * True while the account is inside the rename cooldown, where the only change the server will
   * accept is to the capitalisation of the name already held.
   */
  get casingOnly(): boolean {
    return !!this.profile && !this.profile.CanChangeUsername;
  }

  startUsernameChange(): void {
    if (!this.profile) return;
    this.usernameChangeRequested = true;
    this.usernameMfaVerified = false;
    this.usernameForm.setValue({
      username: this.profile.UsernameDisplay || this.profile.Username,
    });
    this.error = '';
    this.success = '';

    // Deliberately reachable during the cooldown. A casing-only edit moves no lookup key, so the
    // server treats it as free of the cooldown; gating the whole form on CanChangeUsername would
    // make that branch unreachable and leave someone unable to fix their own capitalisation for a
    // month. Suggestions are skipped in that state — a different name is not on offer.
    if (!this.casingOnly) {
      // Fetched here rather than on tab load, so merely viewing the account page spends nothing
      // against the rate-limit budget.
      this.loadSuggestions();
    }
  }

  loadSuggestions(): void {
    this.suggestionsLoading = true;
    this.auth.getUsernameSuggestions().subscribe((suggestions) => {
      this.suggestionsLoading = false;
      this.suggestions = suggestions;
    });
  }

  /** Spends an availability probe on purpose - the user just chose this name. */
  applySuggestion(suggestion: UsernameSuggestion): void {
    this.usernameForm.controls.username.setValue(suggestion.display);
    this.usernameForm.controls.username.markAsTouched();
  }

  /** Which chip, if any, matches what is currently in the field. */
  get normalizedUsername(): string {
    return normalizeUsername(this.usernameForm.getRawValue().username);
  }

  cancelUsernameChange(): void {
    this.usernameChangeRequested = false;
    this.usernameMfaVerified = false;
    this.error = '';
    if (this.profile) {
      this.usernameForm.setValue({
        username: this.profile.UsernameDisplay || this.profile.Username,
      });
    }
  }

  changeUsername(): void {
    if (!this.usernameMfaVerified) {
      return;
    }

    // Deliberately not written back into the control: setValue re-runs the async validator and
    // spends a probe from the rate-limit budget on every submit. The format validator normalises
    // internally, so a whitespace-only value is still caught without the write-back.
    const username = toUsernameDisplay(this.usernameForm.getRawValue().username);
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
          this.usernameForm.setValue({
            username: updated.UsernameDisplay || updated.Username,
          });
          this.success = `Username changed to @${updated.UsernameDisplay || updated.Username}.`;
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
      UsernameDisplay: profile.UsernameDisplay || profile.Username,
      Name: profile.Name ?? null,
      Avatar: profile.Avatar ?? null,
      Usertype: profile.Usertype,
    };
    this.store.dispatch(setUser({ user }));
  }
}
