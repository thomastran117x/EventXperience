import { inject, NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';

import { AccountRecoveryComponent } from './pages/account-recovery/account-recovery.component';
import { DeviceVerifyComponent } from './pages/device-verify/device-verify.component';
import { GoogleCallbackComponent } from './pages/google-callback/google-callback.component';
import { LoginComponent } from './pages/login/login.component';
import { MicrosoftCallbackComponent } from './pages/microsoft-callback/microsoft-callback.component';
import { OAuthRoleComponent } from './pages/oauth-role/oauth-role.component';
import { SignupComponent } from './pages/signup/signup.component';
import { ResetPasswordComponent } from './pages/reset-password/reset-password.component';
import { StepUpVerifyComponent } from './pages/step-up-verify/step-up-verify.component';
import { VerifyComponent } from './pages/verify/verify.component';
import { VerifyEmailChangeComponent } from './pages/verify-email-change/verify-email-change.component';

@NgModule({
  imports: [
    CommonModule,
    ReactiveFormsModule,
    RouterModule.forChild([
      { path: '', redirectTo: 'login', pathMatch: 'full' },
      { path: 'login', component: LoginComponent },
      { path: 'signup', component: SignupComponent },
      { path: 'register', redirectTo: 'signup', pathMatch: 'full' },
      { path: 'recover-account', component: AccountRecoveryComponent },
      { path: 'reset-password', component: ResetPasswordComponent },
      {
        path: 'forgot-password',
        pathMatch: 'full',
        redirectTo: (route) =>
          inject(Router).createUrlTree(['/auth/recover-account'], {
            queryParams: { ...route.queryParams, mode: route.queryParams['mode'] ?? 'password' },
          }),
      },
      {
        path: 'change-password',
        pathMatch: 'full',
        redirectTo: (route) =>
          inject(Router).createUrlTree(['/auth/reset-password'], {
            queryParams: route.queryParams,
          }),
      },
      { path: 'verify', component: VerifyComponent },
      { path: 'verify-email-change', component: VerifyEmailChangeComponent },
      { path: 'device/verify', component: DeviceVerifyComponent },
      { path: 'mfa', component: StepUpVerifyComponent },
      { path: 'oauth/role', component: OAuthRoleComponent },
      { path: 'google', component: GoogleCallbackComponent },
      { path: 'microsoft', component: MicrosoftCallbackComponent },
    ]),
  ],
})
export class AuthModule {}
