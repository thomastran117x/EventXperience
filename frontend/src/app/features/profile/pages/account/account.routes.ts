import { Routes } from '@angular/router';

export const ACCOUNT_ROUTES: Routes = [
  { path: '', redirectTo: 'profile', pathMatch: 'full' },
  {
    path: 'profile',
    loadComponent: () =>
      import('./tabs/profile-tab/profile-tab.component').then((m) => m.ProfileTabComponent),
  },
  {
    path: 'security',
    loadComponent: () =>
      import('./tabs/security-tab/security-tab.component').then((m) => m.SecurityTabComponent),
  },
  {
    path: 'password',
    loadComponent: () =>
      import('./tabs/password-tab/password-tab.component').then((m) => m.PasswordTabComponent),
  },
  {
    path: 'privacy',
    loadComponent: () =>
      import('./tabs/privacy-tab/privacy-tab.component').then((m) => m.PrivacyTabComponent),
  },
  {
    path: 'danger',
    loadComponent: () =>
      import('./tabs/danger-zone-tab/danger-zone-tab.component').then(
        (m) => m.DangerZoneTabComponent,
      ),
  },
];
