using System.Text.Json;

using backend.main.features.auth.device;
using backend.main.features.auth.mfa;
using backend.main.features.auth.mfa.totp;
using backend.main.features.clubs;
using backend.main.features.clubs.discussions;
using backend.main.features.clubs.discussions.replies;
using backend.main.features.clubs.follow;
using backend.main.features.clubs.follow.invitations;
using backend.main.features.clubs.posts;
using backend.main.features.clubs.posts.comments;
using backend.main.features.clubs.posts.search;
using backend.main.features.clubs.reviews;
using backend.main.features.clubs.search;
using backend.main.features.clubs.staff;
using backend.main.features.clubs.versions;
using backend.main.features.events;
using backend.main.features.events.favourites;
using backend.main.features.events.images;
using backend.main.features.events.invitations;
using backend.main.features.events.recentlyviewed;
using backend.main.features.events.registration;
using backend.main.features.events.search;
using backend.main.features.events.series;
using backend.main.features.events.versions;
using backend.main.features.events.waitlist;
using backend.main.features.payment;
using backend.main.features.profile;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace backend.main.infrastructure.database.core
{
    public class AppDatabaseContext : DbContext
    {
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UsernameReservation> UsernameReservations { get; set; } = null!;
        public DbSet<Club> Clubs { get; set; } = null!;
        public DbSet<ClubStaff> ClubStaff { get; set; } = null!;
        public DbSet<ClubVersion> ClubVersions { get; set; } = null!;
        public DbSet<Events> Events { get; set; } = null!;
        public DbSet<EventVersion> EventVersions { get; set; } = null!;
        public DbSet<EventSeries> EventSeries { get; set; } = null!;
        public DbSet<FollowClub> FollowClubs { get; set; } = null!;
        public DbSet<ClubInvitationLink> ClubInvitationLinks { get; set; } = null!;
        public DbSet<Payment> Payments { get; set; } = null!;
        public DbSet<ClubReview> ClubReviews { get; set; } = null!;
        public DbSet<ClubDiscussion> ClubDiscussions { get; set; } = null!;
        public DbSet<ClubDiscussionReply> ClubDiscussionReplies { get; set; } = null!;
        public DbSet<ClubDiscussionReplyReaction> ClubDiscussionReplyReactions { get; set; } = null!;
        public DbSet<Device> Devices { get; set; } = null!;
        public DbSet<SmsMfaEnrollment> SmsMfaEnrollments { get; set; } = null!;
        public DbSet<TotpMfaEnrollment> TotpMfaEnrollments { get; set; } = null!;
        public DbSet<ClubPost> ClubPosts { get; set; } = null!;
        public DbSet<PostComment> PostComments { get; set; } = null!;
        public DbSet<PostCommentReaction> PostCommentReactions { get; set; } = null!;
        public DbSet<EventRegistration> EventRegistrations { get; set; } = null!;
        public DbSet<EventWaitlistEntry> EventWaitlistEntries { get; set; } = null!;
        public DbSet<EventFavourite> EventFavourites { get; set; } = null!;
        public DbSet<RecentlyViewedEvent> RecentlyViewedEvents { get; set; } = null!;
        public DbSet<RecentlyViewedSetting> RecentlyViewedSettings { get; set; } = null!;
        public DbSet<EventImage> EventImages { get; set; } = null!;
        public DbSet<EventInvitation> EventInvitations { get; set; } = null!;
        public DbSet<EventInvitationLink> EventInvitationLinks { get; set; } = null!;
        public DbSet<EventSearchOutbox> EventSearchOutbox { get; set; } = null!;
        public DbSet<ClubSearchOutbox> ClubSearchOutbox { get; set; } = null!;
        public DbSet<ClubPostSearchOutbox> ClubPostSearchOutbox { get; set; } = null!;
        public AppDatabaseContext(DbContextOptions<AppDatabaseContext> options) : base(options) { }

        /// <summary>
        /// Forces every mapped <see cref="DateTime"/> to UTC so Npgsql's
        /// <c>timestamp with time zone</c> mapping never sees a non-UTC kind.
        /// Properties that are deliberately wall-clock opt out in <see cref="OnModelCreating"/>.
        /// </summary>
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            base.ConfigureConventions(configurationBuilder);

            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // MySQL's utf8mb4_0900_ai_ci collation made `=` and the unique indexes below
            // case-insensitive. PostgreSQL is case-sensitive by default, which would break
            // login by username/email and allow case-variant duplicate accounts. citext restores the
            // previous semantics for both equality and the unique index with no query changes.
            modelBuilder.HasPostgresExtension("citext");

            modelBuilder.Entity<User>()
                .Property(u => u.Email)
                .HasColumnType("citext");

            modelBuilder.Entity<User>()
                .Property(u => u.Username)
                .HasColumnType("citext");

            // Deliberately not citext, not indexed, not unique. UsernameDisplay is a presentation
            // string and Username remains the sole lookup key; making this citext or indexed would
            // invite a reader to resolve an account by it, which is the one thing it must never do.
            modelBuilder.Entity<User>()
                .Property(u => u.UsernameDisplay)
                .HasMaxLength(UsernamePolicy.MaxLength);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Username)
                .IsUnique();

            modelBuilder.Entity<UsernameReservation>()
                .HasKey(reservation => reservation.Username);

            modelBuilder.Entity<UsernameReservation>()
                .Property(reservation => reservation.Username)
                .HasColumnType("citext")
                .HasMaxLength(UsernamePolicy.MaxLength);

            modelBuilder.Entity<UsernameReservation>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(reservation => reservation.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UsernameReservation>()
                .HasIndex(reservation => reservation.UserId);

            modelBuilder.Entity<UsernameReservation>()
                .HasIndex(reservation => reservation.ReservedUntilUtc);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.GoogleID)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasIndex(u => u.MicrosoftID)
                .IsUnique();

            modelBuilder.Entity<User>()
                .Property(u => u.IsDisabled)
                .HasDefaultValue(false);

            modelBuilder.Entity<User>()
                .Property(u => u.AuthVersion)
                .HasDefaultValue(1);

            modelBuilder.Entity<Club>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Club>()
                .Property(c => c.Rating)
                .HasPrecision(2, 1);

            var clubGalleryComparer = new ValueComparer<List<string>>(
                (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
                v => v.ToList());

            modelBuilder.Entity<Club>()
                .Property(c => c.GalleryImages)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("json")
                // Nullable in the DB so the column can be added to a table with existing rows;
                // null reads back as an empty list.
                .IsRequired(false)
                .Metadata.SetValueComparer(clubGalleryComparer);

            modelBuilder.Entity<Club>()
                .HasIndex(c => c.UserId);

            modelBuilder.Entity<ClubStaff>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(cs => cs.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubStaff>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(cs => cs.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubStaff>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(cs => cs.GrantedByUserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ClubStaff>()
                .Property(cs => cs.Role)
                .HasConversion<string>()
                .HasMaxLength(32);

            modelBuilder.Entity<ClubStaff>()
                .HasIndex(cs => new { cs.ClubId, cs.UserId })
                .IsUnique();

            modelBuilder.Entity<ClubStaff>()
                .HasIndex(cs => cs.UserId);

            modelBuilder.Entity<ClubVersion>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(v => v.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubVersion>()
                .Property(v => v.ActionType)
                .HasMaxLength(32);

            modelBuilder.Entity<ClubVersion>()
                .Property(v => v.ActorRole)
                .HasMaxLength(64);

            modelBuilder.Entity<ClubVersion>()
                .HasIndex(v => new { v.ClubId, v.VersionNumber })
                .IsUnique();

            modelBuilder.Entity<ClubVersion>()
                .HasIndex(v => v.CreatedAt);

            modelBuilder.Entity<ClubVersion>()
                .HasIndex(v => v.ClubImage);

            modelBuilder.Entity<FollowClub>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<FollowClub>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(f => f.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<FollowClub>()
                .HasIndex(f => f.ClubId);

            modelBuilder.Entity<FollowClub>()
                .HasIndex(f => f.UserId);

            modelBuilder.Entity<ClubInvitationLink>()
                .HasOne(l => l.Club)
                .WithMany()
                .HasForeignKey(l => l.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubInvitationLink>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(l => l.CreatedByUserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ClubInvitationLink>()
                .Property(l => l.TokenHash)
                .HasMaxLength(64);

            modelBuilder.Entity<ClubInvitationLink>()
                .HasIndex(l => l.ClubId);

            modelBuilder.Entity<ClubInvitationLink>()
                .HasIndex(l => l.TokenHash)
                .IsUnique();

            modelBuilder.Entity<Events>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(c => c.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            var tagsComparer = new ValueComparer<List<string>>(
                (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>()),
                v => v.Aggregate(0, (acc, t) => HashCode.Combine(acc, t.GetHashCode())),
                v => v.ToList());

            modelBuilder.Entity<Events>()
                .Property(e => e.Tags)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrEmpty(v)
                        ? new List<string>()
                        : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType("json")
                .Metadata.SetValueComparer(tagsComparer);

            modelBuilder.Entity<Events>()
                .Property(e => e.VenueName)
                .HasMaxLength(100);

            modelBuilder.Entity<Events>()
                .Property(e => e.City)
                .HasMaxLength(100);

            modelBuilder.Entity<Events>()
                .Property(e => e.LifecycleState)
                .HasConversion<int>();

            modelBuilder.Entity<Events>()
                .Property(e => e.PreviousLifecycleState)
                .HasConversion<int?>();

            modelBuilder.Entity<Events>()
                .Property(e => e.WaitlistEnabled)
                .HasDefaultValue(false);

            modelBuilder.Entity<Events>()
                .Property(e => e.WaitlistCount)
                .HasDefaultValue(0);

            modelBuilder.Entity<Events>()
                .HasIndex(e => e.Category);

            // Every manage listing and the public search both filter on lifecycle state, and
            // Paused adds a fifth value to discriminate.
            modelBuilder.Entity<Events>()
                .HasIndex(e => e.LifecycleState);

            modelBuilder.Entity<Events>()
                .HasIndex(e => e.City);

            modelBuilder.Entity<Events>()
                .HasIndex(e => new { e.Latitude, e.Longitude });

            modelBuilder.Entity<EventSeries>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(s => s.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.TimeZoneId)
                .HasMaxLength(64)
                .IsRequired();

            // Wall-clock, not instants: a weekly 7pm series must stay at 7pm local across a DST
            // transition, so these are stored zoneless and must keep DateTimeKind.Unspecified.
            // They opt out of the UTC convention applied in ConfigureConventions.
            modelBuilder.Entity<EventSeries>()
                .Property(s => s.FirstOccurrenceLocalStart)
                .HasColumnType("timestamp without time zone")
                .HasConversion((ValueConverter?)null);

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.EndLocalDate)
                .HasColumnType("timestamp without time zone")
                .HasConversion((ValueConverter?)null);

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.Frequency)
                .HasConversion<int>();

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.EndMode)
                .HasConversion<int>();

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.MonthlyDayPolicy)
                .HasConversion<int>();

            modelBuilder.Entity<EventSeries>()
                .Property(s => s.Status)
                .HasConversion<int>()
                .HasDefaultValue(EventSeriesStatus.Active);

            modelBuilder.Entity<EventSeries>()
                .HasIndex(s => s.ClubId);

            modelBuilder.Entity<EventSeries>()
                .HasIndex(s => s.TemplateEventId);

            // SetNull, deliberately: deleting a series row must never be able to remove
            // occurrences that may already have paying registrants. "Delete series" is an
            // explicit service operation that decides what to do per occurrence; this FK
            // only guarantees the accidental path is safe.
            modelBuilder.Entity<Events>()
                .HasOne<EventSeries>()
                .WithMany()
                .HasForeignKey(e => e.SeriesId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Events>()
                .Property(e => e.SeriesOverridden)
                .HasDefaultValue(false);

            modelBuilder.Entity<Events>()
                .Property(e => e.TimeZoneId)
                .HasMaxLength(64);

            // Unique per series: both MySQL and PostgreSQL treat NULLs as distinct, so every
            // standalone event (SeriesId == null) coexists happily under this index.
            modelBuilder.Entity<Events>()
                .HasIndex(e => new { e.SeriesId, e.OccurrenceIndex })
                .IsUnique();

            modelBuilder.Entity<Events>()
                .HasIndex(e => new { e.SeriesId, e.StartTime });

            modelBuilder.Entity<EventVersion>()
                .HasOne<Events>()
                .WithMany()
                .HasForeignKey(v => v.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventVersion>()
                .Property(v => v.ActionType)
                .HasMaxLength(32);

            modelBuilder.Entity<EventVersion>()
                .Property(v => v.ActorRole)
                .HasMaxLength(64);

            modelBuilder.Entity<EventVersion>()
                .HasIndex(v => new { v.EventId, v.VersionNumber })
                .IsUnique();

            modelBuilder.Entity<EventVersion>()
                .HasIndex(v => v.CreatedAt);

            modelBuilder.Entity<EventImage>()
                .HasOne(ei => ei.Event)
                .WithMany(e => e.Images)
                .HasForeignKey(ei => ei.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventImage>()
                .HasIndex(ei => ei.EventId);

            modelBuilder.Entity<EventImage>()
                .HasIndex(ei => new { ei.EventId, ei.SortOrder });

            modelBuilder.Entity<EventInvitation>()
                .HasOne(i => i.Event)
                .WithMany()
                .HasForeignKey(i => i.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventInvitation>()
                .HasOne(i => i.EventInvitationLink)
                .WithMany(l => l.Invitations)
                .HasForeignKey(i => i.EventInvitationLinkId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.SourceType)
                .HasConversion<string>()
                .HasMaxLength(32);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.LifecycleStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.DeliveryStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.RecipientEmail)
                .HasMaxLength(320);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.RecipientEmailNormalized)
                .HasMaxLength(320);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.ClaimTokenHash)
                .HasMaxLength(128);

            modelBuilder.Entity<EventInvitation>()
                .Property(i => i.DeliveryError)
                .HasMaxLength(1024);

            modelBuilder.Entity<EventInvitation>()
                .HasIndex(i => new { i.EventId, i.LifecycleStatus });

            modelBuilder.Entity<EventInvitation>()
                .HasIndex(i => new { i.RecipientUserId, i.LifecycleStatus });

            modelBuilder.Entity<EventInvitation>()
                .HasIndex(i => new { i.RecipientEmailNormalized, i.LifecycleStatus });

            modelBuilder.Entity<EventInvitation>()
                .HasIndex(i => i.ClaimTokenHash)
                .IsUnique();

            modelBuilder.Entity<EventInvitation>()
                .HasIndex(i => new { i.EventInvitationLinkId, i.RecipientUserId })
                .IsUnique();

            modelBuilder.Entity<EventInvitationLink>()
                .HasOne(l => l.Event)
                .WithMany()
                .HasForeignKey(l => l.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventInvitationLink>()
                .Property(l => l.TokenHash)
                .HasMaxLength(128);

            modelBuilder.Entity<EventInvitationLink>()
                .HasIndex(l => l.TokenHash)
                .IsUnique();

            modelBuilder.Entity<EventInvitationLink>()
                .HasIndex(l => new { l.EventId, l.RevokedAtUtc, l.ExpiresAt });

            modelBuilder.Entity<Payment>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Payment>()
                .HasOne<Events>()
                .WithMany()
                .HasForeignKey(p => p.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Payment>()
                .HasIndex(p => p.UserId);

            modelBuilder.Entity<Payment>()
                .HasIndex(p => p.EventId);

            modelBuilder.Entity<Payment>()
                .HasIndex(p => p.ExternalSessionId)
                .IsUnique();

            modelBuilder.Entity<Payment>()
                .HasIndex(p => p.IdempotencyKey)
                .IsUnique();

            modelBuilder.Entity<ClubReview>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubReview>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(r => r.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubReview>()
                .HasIndex(r => r.ClubId);

            modelBuilder.Entity<ClubReview>()
                .HasIndex(r => r.UserId);

            modelBuilder.Entity<ClubDiscussion>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubDiscussion>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(d => d.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // Serves the newest-first listing of a club's discussions.
            modelBuilder.Entity<ClubDiscussion>()
                .HasIndex(d => new { d.ClubId, d.CreatedAt });

            modelBuilder.Entity<ClubDiscussion>()
                .HasIndex(d => d.UserId);

            modelBuilder.Entity<ClubDiscussionReply>()
                .HasOne<ClubDiscussion>()
                .WithMany()
                .HasForeignKey(r => r.DiscussionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubDiscussionReply>()
                .HasOne<ClubDiscussionReply>()
                .WithMany()
                .HasForeignKey(r => r.ParentReplyId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ClubDiscussionReply>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubDiscussionReply>()
                .HasIndex(r => new { r.DiscussionId, r.ParentReplyId, r.CreatedAt, r.Id });

            modelBuilder.Entity<ClubDiscussionReply>()
                .HasIndex(r => r.UserId);

            modelBuilder.Entity<ClubDiscussionReplyReaction>()
                .HasKey(r => new { r.ReplyId, r.UserId });

            modelBuilder.Entity<ClubDiscussionReplyReaction>()
                .HasOne<ClubDiscussionReply>()
                .WithMany()
                .HasForeignKey(r => r.ReplyId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubDiscussionReplyReaction>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubDiscussionReplyReaction>()
                .Property(r => r.Reaction)
                .HasConversion<string>()
                .HasMaxLength(16);

            modelBuilder.Entity<ClubDiscussionReplyReaction>()
                .HasIndex(r => r.UserId);

            modelBuilder.Entity<Device>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Device>()
                .HasIndex(d => d.DeviceTokenHash)
                .IsUnique();

            modelBuilder.Entity<Device>()
                .HasIndex(d => d.UserId);

            modelBuilder.Entity<Device>()
                .HasIndex(d => new { d.UserId, d.DeviceType, d.ClientName });

            modelBuilder.Entity<SmsMfaEnrollment>()
                .HasKey(enrollment => enrollment.UserId);

            modelBuilder.Entity<SmsMfaEnrollment>()
                .HasOne<User>()
                .WithOne()
                .HasForeignKey<SmsMfaEnrollment>(enrollment => enrollment.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SmsMfaEnrollment>()
                .Property(enrollment => enrollment.PhoneNumber)
                .HasMaxLength(32);

            modelBuilder.Entity<SmsMfaEnrollment>()
                .Property(enrollment => enrollment.IsSmsMfaEnabled)
                .HasDefaultValue(false);

            modelBuilder.Entity<TotpMfaEnrollment>()
                .HasKey(e => e.UserId);

            modelBuilder.Entity<TotpMfaEnrollment>()
                .HasOne<User>()
                .WithOne()
                .HasForeignKey<TotpMfaEnrollment>(e => e.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TotpMfaEnrollment>()
                .Property(e => e.EncryptedSecret)
                .HasMaxLength(512);

            modelBuilder.Entity<TotpMfaEnrollment>()
                .Property(e => e.IsTotpMfaEnabled)
                .HasDefaultValue(false);

            modelBuilder.Entity<TotpMfaEnrollment>()
                .Property(e => e.EncryptionKeyVersion)
                .HasDefaultValue(1);

            modelBuilder.Entity<ClubPost>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubPost>()
                .HasOne<Club>()
                .WithMany()
                .HasForeignKey(p => p.ClubId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ClubPost>()
                .HasIndex(p => p.ClubId);

            modelBuilder.Entity<ClubPost>()
                .HasIndex(p => p.UserId);

            modelBuilder.Entity<PostComment>()
                .HasOne<ClubPost>()
                .WithMany()
                .HasForeignKey(c => c.PostId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PostComment>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PostComment>()
                .HasOne<PostComment>()
                .WithMany()
                .HasForeignKey(c => c.ParentCommentId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<PostComment>()
                .HasIndex(c => new { c.PostId, c.ParentCommentId, c.CreatedAt, c.Id });

            modelBuilder.Entity<PostComment>()
                .HasIndex(c => c.UserId);

            modelBuilder.Entity<PostCommentReaction>()
                .HasKey(r => new { r.CommentId, r.UserId });

            modelBuilder.Entity<PostCommentReaction>()
                .HasOne<PostComment>()
                .WithMany()
                .HasForeignKey(r => r.CommentId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PostCommentReaction>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PostCommentReaction>()
                .Property(r => r.Reaction)
                .HasConversion<string>()
                .HasMaxLength(16);

            modelBuilder.Entity<PostCommentReaction>()
                .HasIndex(r => r.UserId);

            modelBuilder.Entity<EventRegistration>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventRegistration>()
                .HasOne<Events>()
                .WithMany()
                .HasForeignKey(r => r.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventRegistration>()
                .HasIndex(r => r.EventId);

            modelBuilder.Entity<EventRegistration>()
                .HasIndex(r => r.UserId);

            modelBuilder.Entity<EventRegistration>()
                .HasIndex(r => new { r.EventId, r.UserId })
                .IsUnique();

            modelBuilder.Entity<EventFavourite>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventFavourite>()
                .HasOne<Events>()
                .WithMany()
                .HasForeignKey(f => f.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventFavourite>()
                .HasIndex(f => f.EventId);

            // Covers the two hot reads: the user's id set and the pinned list, both of which
            // scan by UserId and want CreatedAt for ordering without a heap lookup.
            modelBuilder.Entity<EventFavourite>()
                .HasIndex(f => new { f.UserId, f.CreatedAt });

            // Unstarring hard-deletes, so unlike the waitlist there is no terminal row to
            // preserve — a plain unique pair is enough, and it is what makes the star
            // idempotent under a double-tap.
            modelBuilder.Entity<EventFavourite>()
                .HasIndex(f => new { f.EventId, f.UserId })
                .IsUnique();

            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(v => v.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            // Cascading from the event means a hard-deleted event drops out of every user's
            // history for free, with nothing left behind to leak its former existence.
            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasOne<Events>()
                .WithMany()
                .HasForeignKey(v => v.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasIndex(v => v.EventId);

            // The hot read: one user's history, newest first. Postgres scans this backwards.
            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasIndex(v => new { v.UserId, v.ViewedAt });

            // What makes a repeat view an UPDATE rather than a second row, and what the
            // concurrent-first-view fallback in RecentlyViewedService relies on catching.
            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasIndex(v => new { v.UserId, v.EventId })
                .IsUnique();

            // Serves the expiry sweep, which scans purely by age across all users. Favourites
            // has no equivalent because nothing sweeps it.
            modelBuilder.Entity<RecentlyViewedEvent>()
                .HasIndex(v => v.ViewedAt);

            modelBuilder.Entity<RecentlyViewedSetting>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RecentlyViewedSetting>()
                .HasIndex(s => s.UserId)
                .IsUnique();

            modelBuilder.Entity<EventWaitlistEntry>()
                .HasOne(w => w.Event)
                .WithMany()
                .HasForeignKey(w => w.EventId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventWaitlistEntry>()
                .HasOne<User>()
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventWaitlistEntry>()
                .Property(w => w.Status)
                .HasConversion<string>()
                .HasMaxLength(24);

            modelBuilder.Entity<EventWaitlistEntry>()
                .Property(w => w.Notes)
                .HasMaxLength(500);

            modelBuilder.Entity<EventWaitlistEntry>()
                .Property(w => w.PhoneNumber)
                .HasMaxLength(32);

            modelBuilder.Entity<EventWaitlistEntry>()
                .Property(w => w.DietaryNeeds)
                .HasMaxLength(500);

            // One row per (event, user) forever: terminal entries are reactivated in place
            // rather than inserted again. PostgreSQL does support partial unique indexes, so
            // this could be narrowed to non-terminal rows, but the reactivate-in-place flow
            // in EventWaitlistService depends on the row surviving.
            modelBuilder.Entity<EventWaitlistEntry>()
                .HasIndex(w => new { w.EventId, w.UserId })
                .IsUnique();

            // Covers both "next in line" (ordered LIMIT) and the position COUNT. The promotion
            // scan additionally filters on EligibilityDeferredUntilUtc, which is low-cardinality
            // and mostly null, so it is left out of the index key.
            modelBuilder.Entity<EventWaitlistEntry>()
                .HasIndex(w => new { w.EventId, w.Status, w.JoinedAtUtc, w.Id });

            // "My waitlists" page.
            modelBuilder.Entity<EventWaitlistEntry>()
                .HasIndex(w => new { w.UserId, w.Status });

            modelBuilder.Entity<EventSearchOutbox>()
                .ToTable("event_search_outbox");

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.Id)
                .HasColumnName("id");

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.AggregateType)
                .HasColumnName("aggregatetype")
                .HasMaxLength(255);

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.AggregateId)
                .HasColumnName("aggregateid")
                .HasMaxLength(255);

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.Type)
                .HasColumnName("type")
                .HasMaxLength(255);

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("json");

            modelBuilder.Entity<EventSearchOutbox>()
                .Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            modelBuilder.Entity<EventSearchOutbox>()
                .HasIndex(e => e.CreatedAt);

            modelBuilder.Entity<ClubSearchOutbox>()
                .ToTable("club_search_outbox");

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.Id)
                .HasColumnName("id");

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.AggregateType)
                .HasColumnName("aggregatetype")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.AggregateId)
                .HasColumnName("aggregateid")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.Type)
                .HasColumnName("type")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("json");

            modelBuilder.Entity<ClubSearchOutbox>()
                .Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            modelBuilder.Entity<ClubSearchOutbox>()
                .HasIndex(e => e.CreatedAt);

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .ToTable("club_post_search_outbox");

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.Id)
                .HasColumnName("id");

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.AggregateType)
                .HasColumnName("aggregatetype")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.AggregateId)
                .HasColumnName("aggregateid")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.Type)
                .HasColumnName("type")
                .HasMaxLength(255);

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("json");

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            modelBuilder.Entity<ClubPostSearchOutbox>()
                .HasIndex(e => e.CreatedAt);
        }
    }
}

