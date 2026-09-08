import { Injectable } from '@angular/core';
import { Observable, from, map, switchMap } from 'rxjs';

import { environment } from '../../../../environments/environment';
import { ApiEnvelope, requireEnvelopeData } from '../../../core/api/models/api-envelope.model';
import { ApiClient } from '../../../core/api/services/api-client.service';
import { AuthTokenService } from '../../../core/api/services/auth-token.service';
import {
  AuthenticatedSessionResponse,
  CurrentUserResponse,
  normalizeAuthenticatedSessionResponse,
  normalizeCurrentUserResponse,
} from '../../../core/models/auth-response.model';

export type SignupRole = 'participant' | 'organizer' | 'volunteer';
export type LoginStepUpMethod = 'sms' | 'email' | 'totp';

export interface LoginRequest {
  username: string;
  password: string;
  rememberMe: boolean;
  captcha: string;
  transport?: 'browser';
  returnUrl?: string;
}

export interface UsernameAvailabilityResponse {
  username: string;
  available: boolean;
}

export interface EmailAvailabilityResponse {
  email: string;
  available: boolean;
}

export interface SignupRequest {
  email: string;
  username: string;
  password: string;
  usertype: SignupRole;
  captcha: string;
}

export interface PasswordRecoveryRequest {
  username: string;
  captcha: string;
}

export interface UsernameRecoveryRequest {
  email: string;
  captcha: string;
}

export interface ResetPasswordRequest {
  password: string;
  code?: string;
  challenge?: string;
}

export interface VerificationChallengeResponse {
  Challenge: string;
  ExpiresAtUtc: string;
}

export interface EmailMfaSettings {
  maskedEmail: string;
  isEnabled: true;
}

export interface SmsMfaSettings {
  enrollmentAvailable: boolean;
  isConfigured: boolean;
  isEnabled: boolean;
  maskedPhoneNumber?: string | null;
  phoneVerifiedAtUtc?: string | null;
  canEnroll: boolean;
  canEnable: boolean;
  canDisable: boolean;
  canRemove: boolean;
}

export interface TotpMfaSettings {
  enrollmentAvailable: boolean;
  isConfigured: boolean;
  isEnabled: boolean;
  enrolledAtUtc?: string | null;
  disabledAtUtc?: string | null;
  canEnroll: boolean;
  canEnable: boolean;
  canDisable: boolean;
  canRemove: boolean;
}

export interface MfaSettingsResponse {
  email: EmailMfaSettings;
  sms: SmsMfaSettings;
  totp: TotpMfaSettings;
}

export interface MfaChallengeResponse {
  Challenge: string;
  ExpiresAtUtc: string;
  Channel: string;
  MaskedDestination: string;
}

export interface TotpEnrollmentStartResponse {
  SecretKey: string;
  QrCodeUri: string;
  ExpiresAtUtc: string;
}

export type SessionMfaMethod = 'sms' | 'email' | 'totp';

export interface SessionMfaOptionsResponse {
  availableMethods: SessionMfaMethod[];
  maskedPhone?: string | null;
  maskedEmail: string;
}

export interface SessionMfaStartResponse {
  selectedMethod: SessionMfaMethod;
  maskedDestination: string;
  expiresAtUtc: string;
  cooldownEndsAtUtc: string;
}

export interface LoginStepUpChallengeResponse {
  Challenge: string;
  ExpiresAtUtc: string;
  AvailableMethods: LoginStepUpMethod[];
  MaskedPhone?: string | null;
  MaskedEmail: string;
}

export interface StartLoginStepUpResponse {
  Challenge: string;
  ExpiresAtUtc: string;
  SelectedMethod: LoginStepUpMethod;
  MaskedDestination: string;
  CooldownEndsAtUtc: string;
  AvailableMethods: LoginStepUpMethod[];
  MaskedPhone?: string | null;
  MaskedEmail: string;
}

export interface OAuthRoleSelectionPayload {
  SignupToken: string;
  Email: string;
  Name: string;
  Provider: string;
}

export interface LoginAuthenticatedResponse {
  Type: 'authenticated';
  Auth: AuthenticatedSessionResponse;
  StepUp?: undefined;
}

export interface LoginRequiresStepUpResponse {
  Type: 'requires_step_up';
  Auth?: undefined;
  StepUp: LoginStepUpChallengeResponse;
}

export type LoginAuthenticationResponse = LoginAuthenticatedResponse | LoginRequiresStepUpResponse;

export interface OAuthAuthenticatedResponse {
  Type: 'authenticated';
  Auth: AuthenticatedSessionResponse;
  StepUp?: undefined;
  RoleSelection?: undefined;
}

export interface OAuthRequiresStepUpResponse {
  Type: 'requires_step_up';
  Auth?: undefined;
  StepUp: LoginStepUpChallengeResponse;
  RoleSelection?: undefined;
}

export interface OAuthRequiresRoleSelectionResponse {
  Type: 'requires_role_selection';
  Auth?: undefined;
  StepUp?: undefined;
  RoleSelection: OAuthRoleSelectionPayload;
}

export type OAuthAuthenticationResponse =
  OAuthAuthenticatedResponse | OAuthRequiresStepUpResponse | OAuthRequiresRoleSelectionResponse;

export const PendingOAuthSignupStorageKey = 'pending_oauth_signup';
export const PendingLoginStepUpStorageKey = 'pending_login_step_up';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly baseUrl = `${environment.backendUrl}/auth`;

  constructor(
    private api: ApiClient,
    private authToken: AuthTokenService,
  ) {}

  login(payload: LoginRequest): Observable<LoginAuthenticationResponse> {
    return this.postWithCsrf<ApiEnvelope<LoginAuthenticationResponse>>(`${this.baseUrl}/login`, {
      ...payload,
      transport: 'browser' as const,
    }).pipe(
      map((res) =>
        this.normalizeLoginAuthenticationResponse(
          this.requireData(res, 'Login response was incomplete.'),
          'Login response was incomplete.',
        ),
      ),
    );
  }

  /**
   * Asks whether a username is still free. Advisory only: the name is not reserved by asking,
   * so signup can still fail with USERNAME_TAKEN if someone claims it first.
   */
  checkUsernameAvailability(username: string): Observable<UsernameAvailabilityResponse> {
    return this.api
      .get<ApiEnvelope<UsernameAvailabilityResponse>>(`${this.baseUrl}/username/availability`, {
        params: { username },
      })
      .pipe(map((res) => this.requireData(res, 'Username availability response was incomplete.')));
  }

  /**
   * Asks whether an email address is still unregistered. Advisory only: the address is not
   * reserved by asking, so signup can still fail with a conflict if someone registers it first.
   */
  checkEmailAvailability(email: string): Observable<EmailAvailabilityResponse> {
    return this.api
      .get<ApiEnvelope<EmailAvailabilityResponse>>(`${this.baseUrl}/email/availability`, {
        params: { email },
      })
      .pipe(map((res) => this.requireData(res, 'Email availability response was incomplete.')));
  }

  signup(payload: SignupRequest): Observable<ApiEnvelope<VerificationChallengeResponse>> {
    return this.postWithCsrf<ApiEnvelope<VerificationChallengeResponse>>(
      `${this.baseUrl}/signup`,
      payload,
    );
  }

  verifyEmail(token: string): Observable<AuthenticatedSessionResponse> {
    return this.postWithCsrf<ApiEnvelope<AuthenticatedSessionResponse>>(`${this.baseUrl}/verify`, {
      token,
      transport: 'browser' as const,
    }).pipe(
      map((res) =>
        this.requireNormalizedSession(
          this.requireData(res, 'Verification response was incomplete.'),
          'Verification response was incomplete.',
        ),
      ),
    );
  }

  /**
   * Applies a pending email change. Takes either proof: the token from the emailed link, or the
   * code and challenge from the page that requested it. Returns no session - confirming signs
   * every session out, so the caller drops local auth state and sends the user to sign in.
   */
  confirmEmailChange(
    payload: { token: string } | { code: string; challenge: string },
  ): Observable<void> {
    return this.postWithCsrf<void>(`${this.baseUrl}/verify/email-change`, payload).pipe(
      map(() => undefined),
    );
  }

  verifyDevice(token: string): Observable<AuthenticatedSessionResponse> {
    return this.api
      .post<ApiEnvelope<AuthenticatedSessionResponse>>(
        `${this.baseUrl}/device/verify`,
        {
          token,
          transport: 'browser' as const,
        },
        {
          withCredentials: true,
        },
      )
      .pipe(
        map((res) =>
          this.requireNormalizedSession(
            this.requireData(res, 'Device verification response was incomplete.'),
            'Device verification response was incomplete.',
          ),
        ),
      );
  }

  startLoginStepUp(
    challenge: string,
    method: LoginStepUpMethod,
  ): Observable<StartLoginStepUpResponse> {
    return this.postWithCsrf<ApiEnvelope<StartLoginStepUpResponse>>(`${this.baseUrl}/mfa/start`, {
      challenge,
      method,
    }).pipe(
      map((res) =>
        this.normalizeStartLoginStepUpResponse(
          this.requireData(res, 'Sign-in verification delivery response was incomplete.'),
          'Sign-in verification delivery response was incomplete.',
        ),
      ),
    );
  }

  verifyLoginStepUp(challenge: string, code: string): Observable<AuthenticatedSessionResponse> {
    return this.postWithCsrf<ApiEnvelope<AuthenticatedSessionResponse>>(
      `${this.baseUrl}/mfa/verify`,
      {
        challenge,
        code,
      },
    ).pipe(
      map((res) =>
        this.requireNormalizedSession(
          this.requireData(res, 'Sign-in verification response was incomplete.'),
          'Sign-in verification response was incomplete.',
        ),
      ),
    );
  }

  verifyTotpLoginStepUp(challenge: string, code: string): Observable<AuthenticatedSessionResponse> {
    return this.postWithCsrf<ApiEnvelope<AuthenticatedSessionResponse>>(
      `${this.baseUrl}/mfa/verify/totp`,
      {
        challenge,
        code,
      },
    ).pipe(
      map((res) =>
        this.requireNormalizedSession(
          this.requireData(res, 'Authenticator verification response was incomplete.'),
          'Authenticator verification response was incomplete.',
        ),
      ),
    );
  }

  googleVerify(
    idToken: string,
    nonce: string,
    returnUrl?: string,
  ): Observable<OAuthAuthenticationResponse> {
    return this.postWithCsrf<ApiEnvelope<OAuthAuthenticationResponse>>(`${this.baseUrl}/google`, {
      token: idToken,
      nonce,
      returnUrl,
      transport: 'browser' as const,
    }).pipe(
      map((res) =>
        this.normalizeOAuthAuthenticationResponse(
          this.requireData(res, 'Google login response was incomplete.'),
          'Google login response was incomplete.',
        ),
      ),
    );
  }

  googleCodeVerify(
    code: string,
    codeVerifier: string,
    redirectUri: string,
    nonce: string,
    returnUrl?: string,
  ): Observable<OAuthAuthenticationResponse> {
    return this.postWithCsrf<ApiEnvelope<OAuthAuthenticationResponse>>(
      `${this.baseUrl}/google/code`,
      {
        code,
        codeVerifier,
        redirectUri,
        nonce,
        returnUrl,
        transport: 'browser' as const,
      },
    ).pipe(
      map((res) =>
        this.normalizeOAuthAuthenticationResponse(
          this.requireData(res, 'Google login response was incomplete.'),
          'Google login response was incomplete.',
        ),
      ),
    );
  }

  microsoftVerify(
    idToken: string,
    nonce: string,
    returnUrl?: string,
  ): Observable<OAuthAuthenticationResponse> {
    return this.postWithCsrf<ApiEnvelope<OAuthAuthenticationResponse>>(
      `${this.baseUrl}/microsoft`,
      {
        token: idToken,
        nonce,
        returnUrl,
        transport: 'browser' as const,
      },
    ).pipe(
      map((res) =>
        this.normalizeOAuthAuthenticationResponse(
          this.requireData(res, 'Microsoft login response was incomplete.'),
          'Microsoft login response was incomplete.',
        ),
      ),
    );
  }

  /**
   * `username` is required when the provider account is new and ignored when it links to one that
   * already exists. The caller cannot tell the two apart, so it always sends one.
   */
  completeOAuthSignup(
    signupToken: string,
    usertype: SignupRole,
    username: string,
  ): Observable<AuthenticatedSessionResponse> {
    return this.postWithCsrf<ApiEnvelope<AuthenticatedSessionResponse>>(
      `${this.baseUrl}/oauth/complete`,
      {
        signupToken,
        usertype,
        username,
        transport: 'browser' as const,
      },
    ).pipe(
      map((res) =>
        this.requireNormalizedSession(
          this.requireData(res, 'OAuth signup completion response was incomplete.'),
          'OAuth signup completion response was incomplete.',
        ),
      ),
    );
  }

  me(): Observable<CurrentUserResponse> {
    return this.getWithCsrf<ApiEnvelope<CurrentUserResponse>>(`${this.baseUrl}/me`).pipe(
      map((res) =>
        this.requireNormalizedCurrentUser(
          this.requireData(res, 'Current user response was incomplete.'),
          'Current user response was incomplete.',
        ),
      ),
    );
  }

  getMfaStatus(): Observable<MfaSettingsResponse> {
    return this.getWithCsrf<ApiEnvelope<MfaSettingsResponse>>(`${this.baseUrl}/mfa`).pipe(
      map((res) => this.requireData(res, 'MFA settings response was incomplete.')),
    );
  }

  getSessionMfaStatus(): Observable<void> {
    // Gated probe: resolves when the session is MFA-verified, otherwise errors
    // with the MFA_REQUIRED code so callers can prompt for verification.
    return this.getWithCsrf<void>(`${this.baseUrl}/mfa/step-up/status`);
  }

  getSessionMfaOptions(): Observable<SessionMfaOptionsResponse> {
    return this.getWithCsrf<ApiEnvelope<SessionMfaOptionsResponse>>(
      `${this.baseUrl}/mfa/step-up/options`,
    ).pipe(
      map((res) => this.requireData(res, 'MFA verification options response was incomplete.')),
    );
  }

  startSessionMfa(method: SessionMfaMethod): Observable<SessionMfaStartResponse> {
    return this.postWithCsrf<ApiEnvelope<SessionMfaStartResponse>>(
      `${this.baseUrl}/mfa/step-up/start`,
      { method },
    ).pipe(
      map((res) => this.requireData(res, 'MFA verification delivery response was incomplete.')),
    );
  }

  verifySessionMfa(method: SessionMfaMethod, code: string): Observable<void> {
    return this.postWithCsrf<void>(`${this.baseUrl}/mfa/step-up/verify`, { method, code });
  }

  startMfaEnrollment(phoneNumber: string): Observable<MfaChallengeResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaChallengeResponse>>(
      `${this.baseUrl}/mfa/sms/enroll/start`,
      {
        phoneNumber,
      },
    ).pipe(map((res) => this.requireData(res, 'SMS MFA challenge response was incomplete.')));
  }

  startMfaEnable(): Observable<MfaChallengeResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaChallengeResponse>>(
      `${this.baseUrl}/mfa/sms/enable/start`,
      {},
    ).pipe(map((res) => this.requireData(res, 'SMS MFA enable response was incomplete.')));
  }

  verifyMfaEnrollment(code: string, challenge: string): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(
      `${this.baseUrl}/mfa/sms/enroll/verify`,
      {
        code,
        challenge,
      },
    ).pipe(map((res) => this.requireData(res, 'SMS MFA verification response was incomplete.')));
  }

  disableMfa(): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(
      `${this.baseUrl}/mfa/sms/disable`,
      {},
    ).pipe(map((res) => this.requireData(res, 'SMS MFA disable response was incomplete.')));
  }

  removeMfa(): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(
      `${this.baseUrl}/mfa/sms/remove`,
      {},
    ).pipe(map((res) => this.requireData(res, 'SMS MFA remove response was incomplete.')));
  }

  startTotpEnrollment(): Observable<TotpEnrollmentStartResponse> {
    return this.postWithCsrf<ApiEnvelope<TotpEnrollmentStartResponse>>(
      `${this.baseUrl}/mfa/totp/enroll/start`,
      {},
    ).pipe(map((res) => this.requireData(res, 'TOTP enrollment response was incomplete.')));
  }

  verifyTotpEnrollment(code: string): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(
      `${this.baseUrl}/mfa/totp/enroll/verify`,
      { code },
    ).pipe(map((res) => this.requireData(res, 'TOTP verification response was incomplete.')));
  }

  enableTotp(code: string): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(`${this.baseUrl}/mfa/totp/enable`, {
      code,
    }).pipe(map((res) => this.requireData(res, 'TOTP enable response was incomplete.')));
  }

  disableTotp(code: string): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(`${this.baseUrl}/mfa/totp/disable`, {
      code,
    }).pipe(map((res) => this.requireData(res, 'TOTP disable response was incomplete.')));
  }

  removeTotp(code: string): Observable<MfaSettingsResponse> {
    return this.postWithCsrf<ApiEnvelope<MfaSettingsResponse>>(`${this.baseUrl}/mfa/totp/remove`, {
      code,
    }).pipe(map((res) => this.requireData(res, 'TOTP remove response was incomplete.')));
  }

  logout(): Observable<void> {
    return this.postWithCsrf<void>(`${this.baseUrl}/logout`, {});
  }

  recoverPassword(
    payload: PasswordRecoveryRequest,
  ): Observable<ApiEnvelope<VerificationChallengeResponse>> {
    return this.postWithCsrf<ApiEnvelope<VerificationChallengeResponse>>(
      `${this.baseUrl}/recovery/password`,
      payload,
    );
  }

  recoverUsername(payload: UsernameRecoveryRequest): Observable<void> {
    return this.postWithCsrf<void>(`${this.baseUrl}/recovery/username`, payload);
  }

  resetPassword(payload: ResetPasswordRequest, token?: string): Observable<void> {
    const query = token ? `?token=${encodeURIComponent(token)}` : '';
    return this.postWithCsrf<void>(`${this.baseUrl}/reset-password${query}`, payload);
  }

  get googleOAuthUrl(): string {
    return `${this.baseUrl}/login/google`;
  }

  get microsoftOAuthUrl(): string {
    return `${this.baseUrl}/login/microsoft`;
  }

  private getWithCsrf<T>(url: string): Observable<T> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.get<T>(url, {
          withCredentials: true,
        }),
      ),
    );
  }

  private postWithCsrf<T>(url: string, body: unknown): Observable<T> {
    return from(this.authToken.ensureCsrfToken()).pipe(
      switchMap(() =>
        this.api.post<T>(url, body, {
          withCredentials: true,
        }),
      ),
    );
  }

  private requireData<T>(response: ApiEnvelope<T>, fallbackMessage: string): T {
    return requireEnvelopeData(response, fallbackMessage);
  }

  private normalizeLoginAuthenticationResponse(
    response: unknown,
    fallbackMessage: string,
  ): LoginAuthenticationResponse {
    const source = this.asRecord(response);
    const type = this.readString(source, 'Type', 'type');

    if (!type) {
      throw new Error(fallbackMessage);
    }

    if (type === 'requires_step_up') {
      return {
        Type: 'requires_step_up',
        StepUp: this.normalizeLoginStepUpChallengeResponse(
          source?.['StepUp'] ?? source?.['stepUp'],
          fallbackMessage,
        ),
      };
    }

    return {
      Type: 'authenticated',
      Auth: this.requireNormalizedSession(source?.['Auth'] ?? source?.['auth'], fallbackMessage),
    };
  }

  private normalizeOAuthAuthenticationResponse(
    response: unknown,
    fallbackMessage: string,
  ): OAuthAuthenticationResponse {
    const source = this.asRecord(response);
    const type = this.readString(source, 'Type', 'type');

    if (!type) {
      throw new Error(fallbackMessage);
    }

    if (type === 'requires_role_selection') {
      return {
        Type: 'requires_role_selection',
        RoleSelection: this.normalizeOAuthRoleSelectionPayload(
          source?.['RoleSelection'] ?? source?.['roleSelection'],
          fallbackMessage,
        ),
      };
    }

    if (type === 'requires_step_up') {
      return {
        Type: 'requires_step_up',
        StepUp: this.normalizeLoginStepUpChallengeResponse(
          source?.['StepUp'] ?? source?.['stepUp'],
          fallbackMessage,
        ),
      };
    }

    return {
      Type: 'authenticated',
      Auth: this.requireNormalizedSession(source?.['Auth'] ?? source?.['auth'], fallbackMessage),
    };
  }

  private normalizeLoginStepUpChallengeResponse(
    value: unknown,
    fallbackMessage: string,
  ): LoginStepUpChallengeResponse {
    const source = this.asRecord(value);
    const challenge = this.readString(source, 'Challenge', 'challenge');
    const expiresAtUtc = this.readString(source, 'ExpiresAtUtc', 'expiresAtUtc');
    const availableMethods = this.readStringArray(source, 'AvailableMethods', 'availableMethods');
    const maskedEmail = this.readString(source, 'MaskedEmail', 'maskedEmail');

    if (!challenge || !expiresAtUtc || availableMethods.length === 0 || !maskedEmail) {
      throw new Error(fallbackMessage);
    }

    return {
      Challenge: challenge,
      ExpiresAtUtc: expiresAtUtc,
      AvailableMethods: availableMethods,
      MaskedPhone: this.readNullableString(source, 'MaskedPhone', 'maskedPhone') ?? null,
      MaskedEmail: maskedEmail,
    };
  }

  private normalizeStartLoginStepUpResponse(
    value: unknown,
    fallbackMessage: string,
  ): StartLoginStepUpResponse {
    const source = this.asRecord(value);
    const challenge = this.readString(source, 'Challenge', 'challenge');
    const expiresAtUtc = this.readString(source, 'ExpiresAtUtc', 'expiresAtUtc');
    const selectedMethod = this.readString(source, 'SelectedMethod', 'selectedMethod');
    const maskedDestination = this.readString(source, 'MaskedDestination', 'maskedDestination');
    const cooldownEndsAtUtc = this.readString(source, 'CooldownEndsAtUtc', 'cooldownEndsAtUtc');
    const availableMethods = this.readStringArray(source, 'AvailableMethods', 'availableMethods');
    const maskedEmail = this.readString(source, 'MaskedEmail', 'maskedEmail');

    if (
      !challenge ||
      !expiresAtUtc ||
      !selectedMethod ||
      !maskedDestination ||
      !cooldownEndsAtUtc ||
      availableMethods.length === 0 ||
      !maskedEmail
    ) {
      throw new Error(fallbackMessage);
    }

    return {
      Challenge: challenge,
      ExpiresAtUtc: expiresAtUtc,
      SelectedMethod: selectedMethod as LoginStepUpMethod,
      MaskedDestination: maskedDestination,
      CooldownEndsAtUtc: cooldownEndsAtUtc,
      AvailableMethods: availableMethods,
      MaskedPhone: this.readNullableString(source, 'MaskedPhone', 'maskedPhone') ?? null,
      MaskedEmail: maskedEmail,
    };
  }

  private normalizeOAuthRoleSelectionPayload(
    value: unknown,
    fallbackMessage: string,
  ): OAuthRoleSelectionPayload {
    const source = this.asRecord(value);
    const signupToken = this.readString(source, 'SignupToken', 'signupToken');
    const email = this.readString(source, 'Email', 'email');
    const provider = this.readString(source, 'Provider', 'provider');
    const name = this.readString(source, 'Name', 'name');

    if (!signupToken || !email || !provider) {
      throw new Error(fallbackMessage);
    }

    return {
      SignupToken: signupToken,
      Email: email,
      Name: name ?? '',
      Provider: provider,
    };
  }

  private requireNormalizedSession(
    value: unknown,
    fallbackMessage: string,
  ): AuthenticatedSessionResponse {
    const session = normalizeAuthenticatedSessionResponse(value);
    if (!session) {
      throw new Error(fallbackMessage);
    }

    return session;
  }

  private requireNormalizedCurrentUser(
    value: unknown,
    fallbackMessage: string,
  ): CurrentUserResponse {
    const user = normalizeCurrentUserResponse(value);
    if (!user) {
      throw new Error(fallbackMessage);
    }

    return user;
  }

  private asRecord(value: unknown): Record<string, unknown> | null {
    return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : null;
  }

  private readString(
    source: Record<string, unknown> | null,
    ...keys: string[]
  ): string | undefined {
    if (!source) {
      return undefined;
    }

    for (const key of keys) {
      const value = source[key];
      if (typeof value === 'string') {
        return value;
      }
    }

    return undefined;
  }

  private readNullableString(
    source: Record<string, unknown> | null,
    ...keys: string[]
  ): string | null | undefined {
    if (!source) {
      return undefined;
    }

    for (const key of keys) {
      const value = source[key];
      if (typeof value === 'string' || value === null) {
        return value;
      }
    }

    return undefined;
  }

  private readStringArray(
    source: Record<string, unknown> | null,
    ...keys: string[]
  ): LoginStepUpMethod[] {
    if (!source) {
      return [];
    }

    for (const key of keys) {
      const value = source[key];
      if (!Array.isArray(value)) {
        continue;
      }

      const methods = value.filter((item): item is LoginStepUpMethod => {
        return item === 'sms' || item === 'email' || item === 'totp';
      });

      if (methods.length > 0) {
        return methods;
      }
    }

    return [];
  }
}
