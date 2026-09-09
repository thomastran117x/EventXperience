import { Routes } from '@angular/router';

import { featureCanMatch } from '../../../../core/features/feature-can-match.guard';
import { FEATURE_KEYS } from '../../../../core/features/feature-flags.types';

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
    // The tab is hidden when the flag is off, but a bookmark would otherwise still load a page
    // whose only setting calls an endpoint the FeatureGate has disabled.
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsRecentlyViewed)],
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
