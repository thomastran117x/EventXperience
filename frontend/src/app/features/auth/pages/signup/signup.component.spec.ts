import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';

import { AuthReturnUrlService } from '../../services/auth-return-url.service';
import {
  AuthService,
  EmailAvailabilityResponse,
  UsernameAvailabilityResponse,
} from '../../services/auth.service';
import { RecaptchaV3Service } from '../../services/recaptcha.service';
import { SignupComponent } from './signup.component';

describe('SignupComponent email availability', () => {
  let fixture: ComponentFixture<SignupComponent>;
  let component: SignupComponent;
  let auth: jasmine.SpyObj<AuthService>;

  beforeEach(async () => {
    auth = jasmine.createSpyObj<AuthService>('AuthService', [
      'signup',
      'checkUsernameAvailability',
      'checkEmailAvailability',
      'getUsernameSuggestions',
    ]);
    auth.getUsernameSuggestions.and.returnValue(
      of([
        { username: 'smartcat23', display: 'SmartCat23' },
        { username: 'braveotter47', display: 'BraveOtter47' },
      ]),
    );
    auth.checkUsernameAvailability.and.returnValue(
      of({ username: 'ada', available: true } as UsernameAvailabilityResponse),
    );

    await TestBed.configureTestingModule({
      imports: [SignupComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: RecaptchaV3Service,
          useValue: jasmine.createSpyObj<RecaptchaV3Service>('RecaptchaV3Service', ['execute']),
        },
        {
          provide: AuthReturnUrlService,
          useValue: jasmine.createSpyObj<AuthReturnUrlService>('AuthReturnUrlService', [
            'captureFromRoute',
            'peek',
            'consume',
          ]),
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SignupComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  function answerWith(result: Observable<EmailAvailabilityResponse>): void {
    auth.checkEmailAvailability.and.returnValue(result);
  }

  function enterEmail(value: string): void {
    component.form.controls.email.setValue(value);
    tick(400);
    fixture.detectChanges();
  }

  function messageText(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  it('announces availability once the API confirms the address is unregistered', fakeAsync(() => {
    answerWith(of({ email: 'ada@example.com', available: true }));

    enterEmail('ada@example.com');

    expect(component.emailAvailable()).toBeTrue();
    expect(messageText()).toContain('That email is available.');
  }));

  it('points a returning user at login when the address is registered', fakeAsync(() => {
    answerWith(of({ email: 'ada@example.com', available: false }));

    enterEmail('ada@example.com');

    expect(component.form.controls.email.hasError('emailTaken')).toBeTrue();
    expect(component.emailAvailable()).toBeFalse();
    expect(messageText()).toContain('That email is already registered.');
    expect(messageText()).toContain('Sign in instead');
  }));

  /**
   * The validator fails open, so a failed or rate-limited probe also leaves the control VALID.
   * Reading validity alone would announce "available" when nothing was ever checked.
   */
  it('claims nothing when the probe fails', fakeAsync(() => {
    answerWith(throwError(() => new Error('network down')));

    enterEmail('ada@example.com');

    expect(component.form.controls.email.valid).toBeTrue();
    expect(component.emailAvailable()).toBeFalse();
    expect(messageText()).not.toContain('That email is available.');
  }));

  it('stops claiming availability once the address is edited again', fakeAsync(() => {
    answerWith(of({ email: 'ada@example.com', available: true }));
    enterEmail('ada@example.com');
    expect(component.emailAvailable()).toBeTrue();

    // Mid-edit the confirmed answer no longer describes the current value.
    component.form.controls.email.setValue('ada@example.co');
    fixture.detectChanges();

    expect(component.emailAvailable()).toBeFalse();
  }));

  it('never probes a value the synchronous validators already reject', fakeAsync(() => {
    answerWith(of({ email: 'ada@example.com', available: true }));

    enterEmail('not-an-address');

    expect(auth.checkEmailAvailability).not.toHaveBeenCalled();
    expect(component.emailAvailable()).toBeFalse();
  }));
});

describe('SignupComponent username format', () => {
  let fixture: ComponentFixture<SignupComponent>;
  let component: SignupComponent;
  let auth: jasmine.SpyObj<AuthService>;

  beforeEach(async () => {
    auth = jasmine.createSpyObj<AuthService>('AuthService', [
      'signup',
      'checkUsernameAvailability',
      'checkEmailAvailability',
      'getUsernameSuggestions',
    ]);
    auth.getUsernameSuggestions.and.returnValue(
      of([
        { username: 'smartcat23', display: 'SmartCat23' },
        { username: 'braveotter47', display: 'BraveOtter47' },
      ]),
    );
    auth.checkUsernameAvailability.and.returnValue(
      of({ username: 'ada', available: true } as UsernameAvailabilityResponse),
    );
    auth.checkEmailAvailability.and.returnValue(
      of({ email: 'ada@example.com', available: true } as EmailAvailabilityResponse),
    );

    await TestBed.configureTestingModule({
      imports: [SignupComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        {
          provide: RecaptchaV3Service,
          useValue: jasmine.createSpyObj<RecaptchaV3Service>('RecaptchaV3Service', ['execute']),
        },
        {
          provide: AuthReturnUrlService,
          useValue: jasmine.createSpyObj<AuthReturnUrlService>('AuthReturnUrlService', [
            'captureFromRoute',
            'peek',
            'consume',
          ]),
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SignupComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  function enterUsername(value: string): void {
    component.form.controls.username.setValue(value);
    component.form.controls.username.markAsTouched();
    tick(400);
    fixture.detectChanges();
  }

  it('shows the server wording for a malformed username', fakeAsync(() => {
    enterUsername('a..b');

    expect(component.usernameFormatMessage()).toContain('must start and end with');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('must start and end with');
  }));

  // The endpoint answers 400 for these and is rate limited at 30/min/IP, so the form must not
  // spend a request to learn what the synchronous validator already knows.
  it('never probes a username the endpoint would reject', fakeAsync(() => {
    enterUsername('ab');
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();

    enterUsername('admin');
    expect(auth.checkUsernameAvailability).not.toHaveBeenCalled();

    expect(component.usernameAvailable()).toBeFalse();
  }));

  it('does not submit while the username is malformed', fakeAsync(() => {
    component.form.controls.email.setValue('ada@example.com');
    component.form.controls.password.setValue('Password123!');
    enterUsername('.ada');

    void component.submit();
    tick();

    expect(auth.signup).not.toHaveBeenCalled();
  }));

  it('fills the username field from a suggestion chip', fakeAsync(() => {
    enterUsername('');
    component.applySuggestion({ username: 'smartcat23', display: 'SmartCat23' });
    tick(400);
    fixture.detectChanges();

    // The display form goes into the field; the async validator normalises before probing.
    expect(component.form.controls.username.value).toBe('SmartCat23');
    expect(auth.checkUsernameAvailability).toHaveBeenCalledWith('smartcat23');
  }));
});
