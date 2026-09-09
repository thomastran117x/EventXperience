export const FEATURE_KEYS = {
  auth: 'auth',
  clubs: 'clubs',
  clubsDiscussions: 'clubs.discussions',
  clubsFollow: 'clubs.follow',
  clubsPosts: 'clubs.posts',
  clubsReviews: 'clubs.reviews',
  clubsVersioning: 'clubs.versioning',
  events: 'events',
  eventsAnalytics: 'events.analytics',
  eventsFavourites: 'events.favourites',
  eventsImages: 'events.images',
  eventsInvitations: 'events.invitations',
  eventsRecentlyViewed: 'events.recentlyviewed',
  eventsRegistration: 'events.registration',
  eventsVersioning: 'events.versioning',
  eventsWaitlist: 'events.waitlist',
  payment: 'payment',
  profile: 'profile',
  profileAdmin: 'profile.admin',
  search: 'search',
  searchReindex: 'search.reindex',
} as const;

export type FeatureKey = (typeof FEATURE_KEYS)[keyof typeof FEATURE_KEYS];
export type FeatureFlags = Partial<Record<FeatureKey, boolean>>;
