import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AuthService } from './auth.service';
import { AuthTokenService } from '../../../core/api/services/auth-token.service';
import {
  ApiClientClientError,
  ApiClientServerError,
  NETWORK_API_ERROR_MESSAGE,
} from '../../../core/api/models/api-client-error.model';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;
  let authToken: jasmine.SpyObj<AuthTokenService>;

  beforeEach(() => {
    authToken = jasmine.createSpyObj<AuthTokenService>('AuthTokenService', ['ensureCsrfToken'], {
      accessToken: null,
      csrfToken: 'csrf-token',
    });
    authToken.ensureCsrfToken.and.resolveTo();

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthTokenService, useValue: authToken },
      ],
    });

    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('posts login credentials after ensuring csrf', fakeAsync(() => {
    let responseBody: unknown;

    service
      .login({
        username: 'event-user',
        password: 'secret123',
        rememberMe: true,
        captcha: 'captcha-token',
        returnUrl: '/dashboard',
      })
      .subscribe((response) => {
        responseBody = response;
      });

    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/login'));
    expect(authToken.ensureCsrfToken).toHaveBeenCalled();
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({
      username: 'event-user',
      password: 'secret123',
      rememberMe: true,
      captcha: 'captcha-token',
      returnUrl: '/dashboard',
      transport: 'browser',
    });

    request.flush({
      success: true,
      message: 'ok',
      data: {
        Type: 'authenticated',
        Auth: {
          AccessToken: 'token',
          ExpiresAtUtc: '2026-06-20T18:00:00Z',
        },
      },
      error: null,
      meta: null,
    });

    expect(responseBody).toEqual({
      Type: 'authenticated',
      Auth: {
        AccessToken: 'token',
        ExpiresAtUtc: '2026-06-20T18:00:00Z',
      },
    });
  }));

  it('posts the chosen username alongside the role when completing an OAuth signup', fakeAsync(() => {
    let responseBody: unknown;

    service
      .completeOAuthSignup('signup-token', 'organizer', 'ada.lovelace')
      .subscribe((response) => {
        responseBody = response;
      });

    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/oauth/complete'));
    expect(authToken.ensureCsrfToken).toHaveBeenCalled();
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({
      signupToken: 'signup-token',
      usertype: 'organizer',
      username: 'ada.lovelace',
      transport: 'browser',
    });

    request.flush({
      success: true,
      message: 'ok',
      data: {
        AccessToken: 'token',
        ExpiresAtUtc: '2026-06-20T18:00:00Z',
      },
      error: null,
      meta: null,
    });

    expect(responseBody).toEqual({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-06-20T18:00:00Z',
    });
  }));

  it('posts step-up start and verify payloads', fakeAsync(() => {
    let startResponse: unknown;
    let verifyResponse: unknown;

    service.startLoginStepUp('challenge-1', 'sms').subscribe((response) => {
      startResponse = response;
    });
    tick();

    const startRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/start'));
    expect(startRequest.request.method).toBe('POST');
    expect(startRequest.request.body).toEqual({ challenge: 'challenge-1', method: 'sms' });
    startRequest.flush({
      success: true,
      message: 'ok',
      data: {
        Challenge: 'challenge-2',
        ExpiresAtUtc: '2026-06-22T15:30:00Z',
        SelectedMethod: 'sms',
        MaskedDestination: '***-***-0123',
        CooldownEndsAtUtc: '2026-06-22T15:16:00Z',
        AvailableMethods: ['sms', 'email', 'totp'],
        MaskedPhone: '***-***-0123',
        MaskedEmail: 'u***@example.com',
      },
      error: null,
      meta: null,
    });

    service.verifyLoginStepUp('challenge-2', '654321').subscribe((response) => {
      verifyResponse = response;
    });
    tick();

    const verifyRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/verify'));
    expect(verifyRequest.request.method).toBe('POST');
    expect(verifyRequest.request.body).toEqual({ challenge: 'challenge-2', code: '654321' });
    verifyRequest.flush({
      success: true,
      message: 'ok',
      data: {
        AccessToken: 'token',
        ExpiresAtUtc: '2026-06-22T15:31:00Z',
        ReturnPath: '/bookings/123',
      },
      error: null,
      meta: null,
    });

    expect(startResponse).toEqual({
      Challenge: 'challenge-2',
      ExpiresAtUtc: '2026-06-22T15:30:00Z',
      SelectedMethod: 'sms',
      MaskedDestination: '***-***-0123',
      CooldownEndsAtUtc: '2026-06-22T15:16:00Z',
      AvailableMethods: ['sms', 'email', 'totp'],
      MaskedPhone: '***-***-0123',
      MaskedEmail: 'u***@example.com',
    });
    expect(verifyResponse).toEqual({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-06-22T15:31:00Z',
      ReturnPath: '/bookings/123',
    });
  }));

  it('posts totp step-up verification payloads', fakeAsync(() => {
    let verifyResponse: unknown;

    service.verifyTotpLoginStepUp('challenge-2', '123456').subscribe((response) => {
      verifyResponse = response;
    });
    tick();

    const verifyRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/verify/totp'));
    expect(verifyRequest.request.method).toBe('POST');
    expect(verifyRequest.request.body).toEqual({ challenge: 'challenge-2', code: '123456' });
    verifyRequest.flush({
      success: true,
      message: 'ok',
      data: {
        AccessToken: 'token',
        ExpiresAtUtc: '2026-06-22T15:31:00Z',
        ReturnPath: '/dashboard',
      },
      error: null,
      meta: null,
    });

    expect(verifyResponse).toEqual({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-06-22T15:31:00Z',
      ReturnPath: '/dashboard',
    });
  }));

  it('posts device verification without bootstrapping csrf', fakeAsync(() => {
    let responseBody: unknown;

    service.verifyDevice('device-token').subscribe((response) => {
      responseBody = response;
    });
    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/device/verify'));
    expect(authToken.ensureCsrfToken).not.toHaveBeenCalled();
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.body).toEqual({
      token: 'device-token',
      transport: 'browser',
    });

    request.flush({
      success: true,
      message: 'ok',
      data: {
        AccessToken: 'token',
        ExpiresAtUtc: '2026-06-30T18:00:00Z',
        ReturnPath: '/dashboard',
      },
      error: null,
      meta: null,
    });

    expect(responseBody).toEqual({
      AccessToken: 'token',
      ExpiresAtUtc: '2026-06-30T18:00:00Z',
      ReturnPath: '/dashboard',
    });
  }));
  it('fetches MFA settings after ensuring csrf', fakeAsync(() => {
    let responseBody: unknown;

    service.getMfaStatus().subscribe((response) => {
      responseBody = response;
    });

    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa'));
    expect(request.request.method).toBe('GET');
    expect(request.request.withCredentials).toBeTrue();

    request.flush({
      success: true,
      message: 'ok',
      data: {
        email: { maskedEmail: 'u***@example.com', isEnabled: true },
        sms: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
        totp: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
      },
      error: null,
      meta: null,
    });

    expect(responseBody).toEqual({
      email: { maskedEmail: 'u***@example.com', isEnabled: true },
      sms: {
        enrollmentAvailable: true,
        isConfigured: false,
        isEnabled: false,
        canEnroll: true,
        canEnable: false,
        canDisable: false,
        canRemove: false,
      },
      totp: {
        enrollmentAvailable: true,
        isConfigured: false,
        isEnabled: false,
        canEnroll: true,
        canEnable: false,
        canDisable: false,
        canRemove: false,
      },
    });
  }));

  it('posts sms enrollment and management payloads', fakeAsync(() => {
    let verified: unknown;
    let disabled: unknown;
    let removed: unknown;
    let enableStart: unknown;

    service.startMfaEnrollment('+14165550123').subscribe();
    tick();
    const startRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/mfa/sms/enroll/start'),
    );
    expect(startRequest.request.body).toEqual({ phoneNumber: '+14165550123' });
    startRequest.flush({
      success: true,
      message: 'ok',
      data: {
        Challenge: 'challenge-1',
        ExpiresAtUtc: '2026-06-22T15:30:00Z',
        Channel: 'sms',
        MaskedDestination: '***-***-0123',
      },
      error: null,
      meta: null,
    });

    service.startMfaEnable().subscribe((response) => {
      enableStart = response;
    });
    tick();
    const enableRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/mfa/sms/enable/start'),
    );
    expect(enableRequest.request.body).toEqual({});
    enableRequest.flush({
      success: true,
      message: 'ok',
      data: {
        Challenge: 'challenge-2',
        ExpiresAtUtc: '2026-06-22T15:40:00Z',
        Channel: 'sms',
        MaskedDestination: '***-***-0123',
      },
      error: null,
      meta: null,
    });

    service.verifyMfaEnrollment('654321', 'challenge-1').subscribe((response) => {
      verified = response;
    });
    tick();
    const verifyRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/mfa/sms/enroll/verify'),
    );
    expect(verifyRequest.request.body).toEqual({ code: '654321', challenge: 'challenge-1' });
    verifyRequest.flush({
      success: true,
      message: 'ok',
      data: {
        email: { maskedEmail: 'u***@example.com', isEnabled: true },
        sms: {
          enrollmentAvailable: true,
          isConfigured: true,
          isEnabled: true,
          maskedPhoneNumber: '***-***-0123',
          phoneVerifiedAtUtc: '2026-06-22T15:31:00Z',
          canEnroll: true,
          canEnable: false,
          canDisable: true,
          canRemove: true,
        },
        totp: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
      },
      error: null,
      meta: null,
    });

    service.disableMfa().subscribe((response) => {
      disabled = response;
    });
    tick();
    const disableRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/sms/disable'));
    expect(disableRequest.request.body).toEqual({});
    disableRequest.flush({
      success: true,
      message: 'ok',
      data: {
        email: { maskedEmail: 'u***@example.com', isEnabled: true },
        sms: {
          enrollmentAvailable: true,
          isConfigured: true,
          isEnabled: false,
          maskedPhoneNumber: '***-***-0123',
          phoneVerifiedAtUtc: '2026-06-22T15:31:00Z',
          canEnroll: true,
          canEnable: true,
          canDisable: false,
          canRemove: true,
        },
        totp: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
      },
      error: null,
      meta: null,
    });

    service.removeMfa().subscribe((response) => {
      removed = response;
    });
    tick();
    const removeRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/sms/remove'));
    expect(removeRequest.request.body).toEqual({});
    removeRequest.flush({
      success: true,
      message: 'ok',
      data: {
        email: { maskedEmail: 'u***@example.com', isEnabled: true },
        sms: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
        totp: {
          enrollmentAvailable: true,
          isConfigured: false,
          isEnabled: false,
          canEnroll: true,
          canEnable: false,
          canDisable: false,
          canRemove: false,
        },
      },
      error: null,
      meta: null,
    });

    expect(enableStart).toEqual({
      Challenge: 'challenge-2',
      ExpiresAtUtc: '2026-06-22T15:40:00Z',
      Channel: 'sms',
      MaskedDestination: '***-***-0123',
    });
    expect(verified).toEqual(
      jasmine.objectContaining({ sms: jasmine.objectContaining({ isEnabled: true }) }),
    );
    expect(disabled).toEqual(
      jasmine.objectContaining({ sms: jasmine.objectContaining({ isEnabled: false }) }),
    );
    expect(removed).toEqual(
      jasmine.objectContaining({ sms: jasmine.objectContaining({ isConfigured: false }) }),
    );
  }));

  it('posts totp management payloads', fakeAsync(() => {
    let startResponse: unknown;
    let verifiedResponse: unknown;
    let enabledResponse: unknown;
    let disabledResponse: unknown;
    let removedResponse: unknown;

    const settingsPayload = {
      email: { maskedEmail: 'u***@example.com', isEnabled: true },
      sms: {
        enrollmentAvailable: true,
        isConfigured: false,
        isEnabled: false,
        canEnroll: true,
        canEnable: false,
        canDisable: false,
        canRemove: false,
      },
      totp: {
        enrollmentAvailable: true,
        isConfigured: true,
        isEnabled: true,
        enrolledAtUtc: '2026-06-22T15:40:00Z',
        canEnroll: false,
        canEnable: false,
        canDisable: true,
        canRemove: true,
      },
    };

    service.startTotpEnrollment().subscribe((response) => {
      startResponse = response;
    });
    tick();
    const startRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/mfa/totp/enroll/start'),
    );
    expect(startRequest.request.body).toEqual({});
    startRequest.flush({
      success: true,
      message: 'ok',
      data: {
        SecretKey: 'BASE32SECRET',
        QrCodeUri: 'otpauth://totp/test',
        ExpiresAtUtc: '2026-06-22T15:40:00Z',
      },
      error: null,
      meta: null,
    });

    service.verifyTotpEnrollment('123456').subscribe((response) => {
      verifiedResponse = response;
    });
    tick();
    const verifyRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/mfa/totp/enroll/verify'),
    );
    expect(verifyRequest.request.body).toEqual({ code: '123456' });
    verifyRequest.flush({
      success: true,
      message: 'ok',
      data: settingsPayload,
      error: null,
      meta: null,
    });

    service.enableTotp('123456').subscribe((response) => {
      enabledResponse = response;
    });
    tick();
    const enableRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/totp/enable'));
    expect(enableRequest.request.body).toEqual({ code: '123456' });
    enableRequest.flush({
      success: true,
      message: 'ok',
      data: settingsPayload,
      error: null,
      meta: null,
    });

    service.disableTotp('123456').subscribe((response) => {
      disabledResponse = response;
    });
    tick();
    const disableRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/totp/disable'));
    expect(disableRequest.request.body).toEqual({ code: '123456' });
    disableRequest.flush({
      success: true,
      message: 'ok',
      data: settingsPayload,
      error: null,
      meta: null,
    });

    service.removeTotp('123456').subscribe((response) => {
      removedResponse = response;
    });
    tick();
    const removeRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/mfa/totp/remove'));
    expect(removeRequest.request.body).toEqual({ code: '123456' });
    removeRequest.flush({
      success: true,
      message: 'ok',
      data: settingsPayload,
      error: null,
      meta: null,
    });

    expect(startResponse).toEqual({
      SecretKey: 'BASE32SECRET',
      QrCodeUri: 'otpauth://totp/test',
      ExpiresAtUtc: '2026-06-22T15:40:00Z',
    });
    expect(verifiedResponse).toEqual(settingsPayload);
    expect(enabledResponse).toEqual(settingsPayload);
    expect(disabledResponse).toEqual(settingsPayload);
    expect(removedResponse).toEqual(settingsPayload);
  }));

  it('surfaces 4xx login failures as typed client errors', fakeAsync(() => {
    let thrown: unknown;

    service
      .login({
        username: 'event-user',
        password: 'secret123',
        rememberMe: false,
        captcha: 'captcha-token',
      })
      .subscribe({
        error: (error) => {
          thrown = error;
        },
      });

    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/login'));
    request.flush(
      {
        success: false,
        message: 'Validation failed.',
        error: {
          code: 'VALIDATION_ERROR',
          details: { username: ['is required'] },
        },
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );

    expect(thrown).toEqual(jasmine.any(ApiClientClientError));
    expect((thrown as ApiClientClientError).message).toBe('Validation failed.');
    expect((thrown as ApiClientClientError).code).toBe('VALIDATION_ERROR');
  }));

  it('posts password recovery by username', fakeAsync(() => {
    service.recoverPassword({ username: 'member-user', captcha: 'captcha-token' }).subscribe();
    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/recovery/password'));
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      username: 'member-user',
      captcha: 'captcha-token',
    });
    request.flush({
      success: true,
      message: 'ok',
      data: { Challenge: 'challenge', ExpiresAtUtc: '2026-08-15T12:00:00Z' },
    });
  }));

  it('posts username recovery by email', fakeAsync(() => {
    service.recoverUsername({ email: 'member@example.com', captcha: 'captcha-token' }).subscribe();
    tick();

    const request = httpMock.expectOne((req) => req.url.endsWith('/auth/recovery/username'));
    expect(request.request.body).toEqual({
      email: 'member@example.com',
      captcha: 'captcha-token',
    });
    request.flush({ message: 'Recovery instructions sent.' });
  }));

  it('posts token and otp password reset proofs', fakeAsync(() => {
    service.resetPassword({ password: 'Password123!' }, 'reset token').subscribe();
    tick();
    const tokenRequest = httpMock.expectOne((req) =>
      req.url.endsWith('/auth/reset-password?token=reset%20token'),
    );
    expect(tokenRequest.request.body).toEqual({ password: 'Password123!' });
    tokenRequest.flush({ message: 'ok' });

    service
      .resetPassword({
        password: 'Password456!',
        code: '123456',
        challenge: 'otp-challenge',
      })
      .subscribe();
    tick();
    const otpRequest = httpMock.expectOne((req) => req.url.endsWith('/auth/reset-password'));
    expect(otpRequest.request.body).toEqual({
      password: 'Password456!',
      code: '123456',
      challenge: 'otp-challenge',
    });
    otpRequest.flush({ message: 'ok' });
  }));

  it('propagates normalized csrf bootstrap failures before the request is sent', fakeAsync(() => {
    let thrown: unknown;
    authToken.ensureCsrfToken.and.rejectWith(
      new ApiClientServerError(NETWORK_API_ERROR_MESSAGE, 0),
    );

    service
      .login({
        username: 'event-user',
        password: 'secret123',
        rememberMe: false,
        captcha: 'captcha-token',
      })
      .subscribe({
        error: (error) => {
          thrown = error;
        },
      });

    tick();

    httpMock.expectNone((req) => req.url.endsWith('/auth/login'));
    expect(thrown).toEqual(jasmine.any(ApiClientServerError));
    expect((thrown as ApiClientServerError).message).toBe(NETWORK_API_ERROR_MESSAGE);
    expect((thrown as ApiClientServerError).status).toBe(0);
  }));
});
