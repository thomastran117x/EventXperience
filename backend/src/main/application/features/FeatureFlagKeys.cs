namespace backend.main.application.features;

public static class FeatureFlagKeys
{
    public const string Auth = "auth";

    /// <summary>
    /// Probabilistic membership filters fronting uniqueness checks. Turning this off makes every
    /// lookup report Unavailable, so callers fall back to querying the database directly.
    /// </summary>
    public const string Bloom = "bloom";

    public const string Clubs = "clubs";
    public const string ClubsDiscussions = "clubs.discussions";
    public const string ClubsFollow = "clubs.follow";
    public const string ClubsPosts = "clubs.posts";
    public const string ClubsReviews = "clubs.reviews";
    public const string ClubsVersioning = "clubs.versioning";
    public const string Events = "events";
    public const string EventsAnalytics = "events.analytics";
    public const string EventsFavourites = "events.favourites";
    public const string EventsImages = "events.images";
    public const string EventsInvitations = "events.invitations";
    public const string EventsRecentlyViewed = "events.recentlyviewed";
    public const string EventsRecurrence = "events.recurrence";
    public const string EventsRegistration = "events.registration";
    public const string EventsVersioning = "events.versioning";
    public const string EventsWaitlist = "events.waitlist";
    public const string Payment = "payment";
    public const string Profile = "profile";
    public const string ProfileAdmin = "profile.admin";
    public const string Search = "search";
    public const string SearchReindex = "search.reindex";
    public const string Storage = "storage";
    public const string StorageOrphanCleanup = "storage.orphan-cleanup";
}
