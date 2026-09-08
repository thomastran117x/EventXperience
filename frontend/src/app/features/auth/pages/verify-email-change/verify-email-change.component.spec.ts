import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { fakeActivatedRoute } from '@testing';

import { VerifyEmailChangeComponent } from './verify-email-change.component';
import { ApiClientClientError } from '../../../../core/api/models/api-client-error.model';
import { AuthTokenService } from '../../../../core/api/services/auth-token.service';
import { AuthService } from '../../services/auth.service';

type AuthStub = Pick<AuthService, 'confirmEmailChange'>;
type AuthTokenStub = Pick<AuthTokenService, 'logoutLocal'>;

describe('VerifyEmailChangeComponent', () => {
  let fixture: ComponentFixture<VerifyEmailChangeComponent>;
  let component: VerifyEmailChangeComponent;
  let auth: jasmine.SpyObj<AuthStub>;
  let authToken: jasmine.SpyObj<AuthTokenStub>;
  let router: Router;

  async function setup(queryParams: Record<string, string> = { token: 'link-token' }) {
    auth = jasmine.createSpyObj<AuthStub>('AuthService', ['confirmEmailChange']);
    authToken = jasmine.createSpyObj<AuthTokenStub>('AuthTokenService', ['logoutLocal']);

    await TestBed.resetTestingModule()
      .configureTestingModule({
        imports: [VerifyEmailChangeComponent],
        providers: [
          { provide: AuthService, useValue: auth },
          { provide: AuthTokenService, useValue: authToken },
          provideRouter([]),
          { provide: ActivatedRoute, useValue: fakeActivatedRoute({ queryParams }).route },
        ],
      })
      .compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(VerifyEmailChangeComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('waits for the reader to confirm rather than firing on load', async () => {
    await setup();

    expect(component.status()).toBe('ready');
    expect(component.hasToken).toBeTrue();
    expect(auth.confirmEmailChange).not.toHaveBeenCalled();
  });

  it('reports a link with no token, and offers no way to submit one', async () => {
    await setup({});

    expect(component.status()).toBe('error');
    expect(component.hasToken).toBeFalse();

    component.confirm();

    expect(auth.confirmEmailChange).not.toHaveBeenCalled();
  });

  /**
   * The confirmation revokes every session server-side, so whatever token this browser holds is
   * already dead. Clearing it locally is what keeps the app from acting on it.
   */
  it('clears local auth and routes to login on success', fakeAsync(async () => {
    await setup();
    auth.confirmEmailChange.and.returnValue(of(undefined));
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);

    component.confirm();

    expect(auth.confirmEmailChange).toHaveBeenCalledWith({ token: 'link-token' });
    expect(component.status()).toBe('success');
    expect(authToken.logoutLocal).toHaveBeenCalled();

    tick(2000);
    expect(navigate).toHaveBeenCalledWith(['/auth/login']);
  }));

  it('surfaces the server message and allows a retry', async () => {
    await setup();
    auth.confirmEmailChange.and.returnValue(
      throwError(() => new ApiClientClientError('This link has expired.', 401, 'UNAUTHORIZED')),
    );

    component.confirm();

    expect(component.status()).toBe('error');
    expect(component.message()).toContain('This link has expired.');
    expect(authToken.logoutLocal).not.toHaveBeenCalled();

    // The button stays live, so a transient failure is recoverable without reopening the email.
    auth.confirmEmailChange.and.returnValue(of(undefined));
    component.confirm();
    expect(component.status()).toBe('success');
  });

  it('ignores a second click while a confirmation is in flight', async () => {
    await setup();
    auth.confirmEmailChange.and.returnValue(of(undefined));

    component.status.set('loading');
    component.confirm();

    expect(auth.confirmEmailChange).not.toHaveBeenCalled();
  });
});
