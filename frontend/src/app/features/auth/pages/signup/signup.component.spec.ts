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
    ]);
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
