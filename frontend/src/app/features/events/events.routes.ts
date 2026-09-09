import { Routes } from '@angular/router';

import { featureCanMatch } from '../../core/features/feature-can-match.guard';
import { FEATURE_KEYS } from '../../core/features/feature-flags.types';
import { authenticatedUserGuard } from '../../core/guards/authenticated-user.guard';

export const EVENTS_ROUTES: Routes = [
  {
    path: 'invite',
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsInvitations)],
    loadComponent: () =>
      import('./pages/invite/event-invite.component').then((m) => m.EventInviteComponent),
  },
  {
    path: 'me/invited',
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsInvitations)],
    loadComponent: () =>
      import('./pages/my-invites/my-invites.component').then((m) => m.MyInvitesComponent),
  },
  {
    path: 'me/pinned',
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsFavourites)],
    loadComponent: () =>
      import('./pages/my-pinned/my-pinned.component').then((m) => m.MyPinnedComponent),
  },
  {
    path: 'me/recent',
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsRecentlyViewed)],
    loadComponent: () =>
      import('./pages/my-recent/my-recent.component').then((m) => m.MyRecentComponent),
  },
  {
    path: 'me/waitlisted',
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsWaitlist)],
    loadComponent: () =>
      import('./pages/my-waitlists/my-waitlists.component').then((m) => m.MyWaitlistsComponent),
  },
  {
    path: ':eventId/waitlist/manage',
    canActivate: [authenticatedUserGuard],
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsWaitlist)],
    loadComponent: () =>
      import('./pages/manage-waitlist/manage-event-waitlist.component').then(
        (m) => m.ManageEventWaitlistComponent,
      ),
  },
  {
    path: ':eventId/invitations/manage',
    canActivate: [authenticatedUserGuard],
    canMatch: [featureCanMatch(FEATURE_KEYS.eventsInvitations)],
    loadComponent: () =>
      import('./pages/manage-invitations/manage-event-invitations.component').then(
        (m) => m.ManageEventInvitationsComponent,
      ),
  },
  {
    path: ':eventId',
    loadComponent: () =>
      import('./pages/detail/event-detail.component').then((m) => m.EventDetailComponent),
  },
  {
    path: '',
    loadComponent: () =>
      import('./pages/search/events-search.component').then((m) => m.EventsSearchComponent),
    pathMatch: 'full',
  },
];
