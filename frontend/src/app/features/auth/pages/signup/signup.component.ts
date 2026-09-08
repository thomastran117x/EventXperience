import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize } from 'rxjs/operators';
import { environment } from '@environments/environment';
import { PillComponent } from '@common/pill/pill.component';
import { AuthService, SignupRole } from '../../services/auth.service';
import { RecaptchaV3Service } from '../../services/recaptcha.service';
import { getApiClientMessage } from '../../../../core/api/models/api-client-error.model';
import { AuthReturnUrlService } from '../../services/auth-return-url.service';
import {
  emailAvailabilityValidator,
  normalizeEmail,
} from '../../validators/email-availability.validator';
import {
  normalizeUsername,
  usernameAvailabilityValidator,
} from '../../validators/username-availability.validator';
import { usernameFormatValidator } from '../../validators/username-format.validator';

@Component({
  selector: 'app-signup',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, PillComponent],
  templateUrl: './signup.component.html',
  styleUrls: ['./signup.component.css'],
})
export class SignupComponent {
  // Injected as a field rather than a constructor parameter: the form initialiser below needs it,
  // and with ES2022 class fields those run before the constructor body assigns parameters.
  private readonly auth = inject(AuthService);

  private readonly fb = new FormBuilder();

  /** The last username the API actually confirmed as free; null whenever we did not get an answer. */
  private readonly confirmedAvailable = signal<string | null>(null);

  /** The last email the API actually confirmed as unregistered; null when we did not get an answer. */
  private readonly confirmedEmailAvailable = signal<string | null>(null);

  readonly siteKey = environment.googleSiteKey;
  readonly roleOptions: Array<{ value: SignupRole; label: string }> = [
    { value: 'participant', label: 'Participant' },
    { value: 'organizer', label: 'Organizer' },
    { value: 'volunteer', label: 'Volunteer' },
  ];

  readonly form = this.fb.nonNullable.group({
    email: this.fb.nonNullable.control('', {
      validators: [Validators.required, Validators.email],
      asyncValidators: [
        emailAvailabilityValidator(this.auth, (email) => this.confirmedEmailAvailable.set(email)),
      ],
    }),
    username: this.fb.nonNullable.control('', {
      validators: [Validators.required, usernameFormatValidator],
      asyncValidators: [
        usernameAvailabilityValidator(this.auth, (username) =>
          this.confirmedAvailable.set(username),
        ),
      ],
    }),
    password: this.fb.nonNullable.control('', [Validators.required, Validators.minLength(8)]),
    usertype: this.fb.nonNullable.control<SignupRole>('participant', [Validators.required]),
  });

  loading = false;
  submitted = false;
  error = '';
  success = '';

  private readonly usernameStatus = signal(this.form.controls.username.status);
  private readonly emailStatus = signal(this.form.controls.email.status);

  readonly usernameChecking = computed(() => this.usernameStatus() === 'PENDING');
  readonly emailChecking = computed(() => this.emailStatus() === 'PENDING');

  /**
   * Same rule as usernameAvailable(): the validator fails open, so a failed or rate-limited probe
   * also leaves the control VALID. Only a confirmed answer for the current value may claim the
   * address is free.
   */
  readonly emailAvailable = computed(() => {
    const confirmed = this.confirmedEmailAvailable();
    return (
      this.emailStatus() === 'VALID' &&
      confirmed !== null &&
      confirmed === normalizeEmail(this.form.controls.email.value)
    );
  });

  /**
   * Only claim availability for a name the API actually confirmed. The validator fails open, so
   * a failed or rate-limited probe also leaves the control VALID — reading validity alone would
   * announce "available" when nothing was ever checked.
   */
  readonly usernameAvailable = computed(() => {
    const confirmed = this.confirmedAvailable();
    return (
      this.usernameStatus() === 'VALID' &&
      confirmed !== null &&
      confirmed === normalizeUsername(this.form.controls.username.value)
    );
  });

  /** The server's own wording for why this value would be rejected, or null when it is fine. */
  usernameFormatMessage(): string | null {
    return this.form.controls.username.errors?.['usernameFormat']?.message ?? null;
  }

  constructor(
    private recaptcha: RecaptchaV3Service,
    private route: ActivatedRoute,
    private authReturnUrl: AuthReturnUrlService,
  ) {
    this.form.controls.username.statusChanges
      .pipe(takeUntilDestroyed())
      .subscribe((status) => this.usernameStatus.set(status));
    this.form.controls.email.statusChanges
      .pipe(takeUntilDestroyed())
      .subscribe((status) => this.emailStatus.set(status));
  }

  ngOnInit(): void {
    this.authReturnUrl.captureFromRoute(this.route);
  }

  async submit(): Promise<void> {
    this.submitted = true;
    this.error = '';
    this.success = '';

    if (this.form.invalid) {
      return;
    }

    this.loading = true;

    try {
      const captcha = await this.recaptcha.execute(this.siteKey, 'signup');
      const values = this.form.getRawValue();
      // Deliberately not written back into the controls. setValue re-runs the async validators
      // (emitEvent: false suppresses the events but still issues the request), spending a probe
      // from the rate-limit budget on every submit and leaving the fields PENDING under a form
      // that has already been submitted. The payload below carries the exact values being sent,
      // so echoing them into the inputs bought nothing.
      const username = normalizeUsername(values.username);
      this.auth
        .signup({
          ...values,
          username,
          captcha,
        })
        .pipe(finalize(() => (this.loading = false)))
        .subscribe({
          next: () => {
            this.success =
              'Verification email sent. Check your inbox to finish creating your account.';
          },
          error: (err) => {
            this.error = getApiClientMessage(err, 'Signup failed.');
          },
        });
    } catch (err: any) {
      this.loading = false;
      this.error = err?.message || 'Captcha failed to initialize.';
    }
  }
}
