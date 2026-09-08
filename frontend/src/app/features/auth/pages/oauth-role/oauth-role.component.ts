import { isPlatformBrowser } from '@angular/common';
import { Component, inject, PLATFORM_ID, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { SessionManagerService } from '../../../../core/services/session-manager.service';
import {
  AuthService,
  OAuthRoleSelectionPayload,
  PendingOAuthSignupStorageKey,
  SignupRole,
} from '../../services/auth.service';
import { AuthReturnUrlService } from '../../services/auth-return-url.service';
import {
  normalizeUsername,
  usernameAvailabilityValidator,
} from '../../validators/username-availability.validator';
import {
  suggestUsernameFromEmail,
  usernameFormatValidator,
} from '../../validators/username-format.validator';

@Component({
  selector: 'app-oauth-role',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './oauth-role.component.html',
  styleUrls: ['./oauth-role.component.css'],
})
export class OAuthRoleComponent {
  private platformId = inject(PLATFORM_ID);
  private fb = inject(FormBuilder);

  // Injected as a field rather than a constructor parameter: the form initialiser below needs it,
  // and with ES2022 class fields those run before the constructor body assigns parameters.
  private readonly auth = inject(AuthService);

  /** The last username the API actually confirmed as free; null whenever we did not get an answer. */
  private confirmedAvailable: string | null = null;

  readonly roleOptions: Array<{ value: SignupRole; label: string; description: string }> = [
    {
      value: 'participant',
      label: 'Participant',
      description: 'Join events, discover clubs, and follow what interests you.',
    },
    {
      value: 'organizer',
      label: 'Organizer',
      description: 'Create events, manage communities, and publish updates.',
    },
    {
      value: 'volunteer',
      label: 'Volunteer',
      description: 'Help run events and support organizers on the ground.',
    },
  ];

  readonly form = this.fb.nonNullable.group({
    username: this.fb.nonNullable.control('', {
      validators: [Validators.required, usernameFormatValidator],
      asyncValidators: [
        usernameAvailabilityValidator(
          this.auth,
          (username) => (this.confirmedAvailable = username),
        ),
      ],
    }),
    usertype: this.fb.nonNullable.control<SignupRole>('participant', [Validators.required]),
  });

  readonly status = signal<'ready' | 'loading' | 'error'>('ready');
  readonly message = signal('Choose how you want to use EventXperience.');
  readonly pending = signal<OAuthRoleSelectionPayload | null>(null);

  submitted = false;

  constructor(
    private sessionManager: SessionManagerService,
    private router: Router,
    private authReturnUrl: AuthReturnUrlService,
  ) {}

  usernameChecking(): boolean {
    return this.form.controls.username.pending;
  }

  /**
   * Only claim availability for a name the API actually confirmed. The validator fails open, so a
   * failed or rate-limited probe also leaves the control VALID - reading validity alone would
   * announce "available" when nothing was ever checked.
   */
  usernameAvailable(): boolean {
    const control = this.form.controls.username;
    return (
      control.valid &&
      this.confirmedAvailable !== null &&
      this.confirmedAvailable === normalizeUsername(control.value)
    );
  }

  /** The server's own wording for why this value would be rejected, or null when it is fine. */
  usernameFormatMessage(): string | null {
    return this.form.controls.username.errors?.['usernameFormat']?.message ?? null;
  }

  ngOnInit(): void {
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    const raw = sessionStorage.getItem(PendingOAuthSignupStorageKey);
    if (!raw) {
      this.status.set('error');
      this.message.set('Your OAuth signup session was not found. Please start again.');
      return;
    }

    try {
      const parsed = JSON.parse(raw) as OAuthRoleSelectionPayload;
      if (!parsed.SignupToken || !parsed.Email || !parsed.Provider) {
        throw new Error('Incomplete OAuth signup session.');
      }

      this.pending.set(parsed);
      // A starting point only; the user can replace it, and the probe below decides if it is free.
      this.form.controls.username.setValue(suggestUsernameFromEmail(parsed.Email));
    } catch {
      sessionStorage.removeItem(PendingOAuthSignupStorageKey);
      this.status.set('error');
      this.message.set('Your OAuth signup session is invalid. Please start again.');
    }
  }

  submit(): void {
    this.submitted = true;
    if (this.status() === 'loading' || this.form.invalid) {
      return;
    }

    const pending = this.pending();
    if (!pending) {
      this.status.set('error');
      this.message.set('Your OAuth signup session is missing. Please start again.');
      return;
    }

    this.status.set('loading');
    this.message.set('Completing your account setup...');

    const values = this.form.getRawValue();
    // Deliberately not written back into the control: setValue re-runs the async validator and
    // spends a probe from the rate-limit budget, for a value already being sent.
    this.auth
      .completeOAuthSignup(pending.SignupToken, values.usertype, normalizeUsername(values.username))
      .subscribe({
        next: async (session) => {
          try {
            await this.sessionManager.bootstrapSession(session);
            sessionStorage.removeItem(PendingOAuthSignupStorageKey);
            this.status.set('ready');
            this.message.set('Your account is ready. Redirecting you back...');
            const target = this.authReturnUrl.consume(session.ReturnPath ?? '/dashboard');
            setTimeout(() => this.router.navigateByUrl(target), 800);
          } catch (err: any) {
            this.status.set('error');
            this.message.set(
              getApiClientMessage(err, 'We could not complete your signup. Please try again.'),
            );
          }
        },
        error: (err) => {
          this.status.set('error');
          this.message.set(
            getApiClientMessage(err, 'We could not complete your signup. Please try again.'),
          );
        },
      });
  }
}
