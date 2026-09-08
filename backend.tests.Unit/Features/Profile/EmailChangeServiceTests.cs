using backend.main.features.auth;
using backend.main.features.auth.contracts;
using backend.main.features.auth.notifications;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.events.invitations;
using backend.main.features.profile;
using backend.main.features.profile.contracts;
using backend.main.features.profile.email;
using backend.main.seeders;
using backend.main.shared.exceptions.http;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Profile;

public class EmailChangeServiceTests
{
    private const int UserId = 42;
    private const string CurrentEmail = "ada@example.com";
    private const string NewEmail = "ada.lovelace@example.com";
    private const string CorrectPassword = "correct-horse";

    // ------------------------------------------------------------------ request

    [Fact]
    public async Task RequestChangeAsync_ShouldMailBothAddresses_AndChangeNothingYet()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();

        var challenge = await harness.Service.RequestChangeAsync(UserId, NewEmail, CorrectPassword);

        challenge.Challenge.Should().Be("challenge-token");

        // The proof goes to the address being claimed...
        harness.Notifications.Verify(
            n => n.SendEmailChangeVerificationAsync(NewEmail, "link-token", "123456", "Ada"),
            Times.Once);

        // ...and the address being left gets told, but is given nothing it could act on.
        harness.Notifications.Verify(
            n => n.SendEmailChangeRequestedAsync(CurrentEmail, NewEmail, "Ada"),
            Times.Once);

        harness.Repository.Verify(
            r => r.ChangeEmailAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldSucceed_WhenTheOldInboxCannotBeNotified()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.Notifications
            .Setup(n => n.SendEmailChangeRequestedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, CorrectPassword);

        // The confirmation has already been sent by this point; failing the request would leave a
        // live token the caller was never told about.
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldRejectTheCurrentAddress()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();

        var act = () => harness.Service.RequestChangeAsync(UserId, "ADA@Example.com", CorrectPassword);

        await act.Should().ThrowAsync<BadRequestException>();
        harness.Tokens.Verify(
            t => t.GenerateEmailChangeArtifactsAsync(It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// SeedAccountBypassPolicy matches the seed domain by suffix and hands out captcha and MFA
    /// bypasses to anything under it, so a verified change into the domain would be a way to grant
    /// yourself one.
    /// </summary>
    [Fact]
    public async Task RequestChangeAsync_ShouldRejectTheSeedDomain()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();

        var act = () => harness.Service.RequestChangeAsync(
            UserId,
            "attacker" + SeedCatalogConstants.SeedEmailDomain,
            CorrectPassword);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*reserved*");
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldRejectAnIncorrectPassword()
    {
        var harness = new Harness();
        harness.WithPasswordAccount(passwordHash: BCrypt.Net.BCrypt.HashPassword("actual", 4));

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, "guessed");

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldRequireAPassword_WhenTheAccountHasOne()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, currentPassword: null);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    /// <summary>
    /// An OAuth-only account has no password to re-verify; MFA step-up is what gated the endpoint.
    /// </summary>
    [Fact]
    public async Task RequestChangeAsync_ShouldNotRequireAPassword_ForAnOAuthOnlyAccount()
    {
        var harness = new Harness();
        harness.WithOAuthOnlyAccount();

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, currentPassword: null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldRejectAnAddressThatIsAlreadyRegistered()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithRegisteredAddress(NewEmail);

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, CorrectPassword);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task RequestChangeAsync_ShouldRejectADisabledAccount()
    {
        var harness = new Harness();
        harness.WithPasswordAccount(isDisabled: true);

        var act = () => harness.Service.RequestChangeAsync(UserId, NewEmail, CorrectPassword);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // ------------------------------------------------------------------ confirm

    [Fact]
    public async Task ConfirmAsync_ShouldApplyTheChange_AndRevokeEverySession()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithSuccessfulChange();

        await harness.Service.ConfirmAsync(new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        harness.Repository.Verify(
            r => r.ChangeEmailAsync(UserId, NewEmail, harness.UtcNow),
            Times.Once);

        // The address is an access token claim, so leaving refresh sessions alive would let a
        // stale identity keep minting new tokens.
        harness.Tokens.Verify(t => t.RevokeAllRefreshSessionsAsync(UserId), Times.Once);
        harness.Cache.Verify(c => c.RemoveAsync($"user:{UserId}"), Times.Once);
    }

    [Fact]
    public async Task ConfirmAsync_ShouldRecordTheNewAddressInTheFilter()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithSuccessfulChange();

        await harness.Service.ConfirmAsync(new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        harness.Availability.Verify(
            a => a.MarkRegisteredAsync(NewEmail, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAsync_ShouldRelinkInvitationsForBothAddresses()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithSuccessfulChange();

        await harness.Service.ConfirmAsync(new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        harness.Invitations.Verify(
            i => i.RelinkForEmailChangeAsync(UserId, CurrentEmail, NewEmail),
            Times.Once);
    }

    /// <summary>
    /// The address was free when the change was requested, which says nothing about 30 minutes
    /// later.
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_ShouldRejectAnAddressClaimedSinceTheRequest()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithRegisteredAddress(NewEmail);

        var act = () => harness.Service.ConfirmAsync(
            new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        await act.Should().ThrowAsync<ConflictException>();
        harness.Repository.Verify(
            r => r.ChangeEmailAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [Fact]
    public async Task ConfirmAsync_ShouldSurfaceAConflict_WhenTheWriteLosesTheRace()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.Repository
            .Setup(r => r.ChangeEmailAsync(UserId, NewEmail, It.IsAny<DateTime>()))
            .ReturnsAsync(new EmailChangeRecord(EmailChangeStatus.Unavailable));

        var act = () => harness.Service.ConfirmAsync(
            new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task ConfirmAsync_ShouldRejectADisabledAccount()
    {
        var harness = new Harness();
        harness.WithPasswordAccount(isDisabled: true);

        var act = () => harness.Service.ConfirmAsync(
            new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ConfirmAsync_ShouldRejectAnAccountThatNoLongerExists()
    {
        var harness = new Harness();
        harness.Repository
            .Setup(r => r.GetAuthByIdAsync(UserId))
            .ReturnsAsync((UserAuthRecord?)null);

        var act = () => harness.Service.ConfirmAsync(
            new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task ConfirmAsync_ShouldSucceed_WhenTheNotificationsCannotBeSent()
    {
        var harness = new Harness();
        harness.WithPasswordAccount();
        harness.WithSuccessfulChange();
        harness.Notifications
            .Setup(n => n.SendEmailChangedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));

        var act = () => harness.Service.ConfirmAsync(
            new PendingEmailChange(UserId, NewEmail, DateTime.UtcNow));

        // The change has already committed and the user has already been signed out; a mail
        // failure cannot undo either.
        await act.Should().NotThrowAsync();
    }

    // ------------------------------------------------------------------ harness

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class Harness
    {
        public readonly Mock<IAuthUserRepository> Repository = new();
        public readonly Mock<ITokenService> Tokens = new();
        public readonly Mock<IEmailAvailabilityService> Availability = new();
        public readonly Mock<IAuthNotificationService> Notifications = new();
        public readonly Mock<IEventInvitationService> Invitations = new();
        public readonly Mock<IRefreshAheadCache> Cache = new();
        public readonly DateTime UtcNow = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        public readonly EmailChangeService Service;

        public Harness()
        {
            Tokens
                .Setup(t => t.GenerateEmailChangeArtifactsAsync(It.IsAny<int>(), It.IsAny<string>()))
                .ReturnsAsync(new VerificationArtifacts
                {
                    LinkToken = "link-token",
                    OtpChallenge = new VerificationOtpChallenge
                    {
                        Code = "123456",
                        Challenge = "challenge-token",
                        ExpiresAtUtc = UtcNow.AddMinutes(30),
                    },
                    Purpose = VerificationPurpose.ChangeEmail,
                });

            Service = new EmailChangeService(
                Repository.Object,
                Tokens.Object,
                Availability.Object,
                Notifications.Object,
                Invitations.Object,
                Cache.Object,
                new FixedTimeProvider(UtcNow));
        }

        // Work factor 4 keeps the suite fast; the production hash is 12.
        public void WithPasswordAccount(bool isDisabled = false, string? passwordHash = null) =>
            SetAccount(passwordHash ?? BCrypt.Net.BCrypt.HashPassword(CorrectPassword, 4), isDisabled);

        public void WithOAuthOnlyAccount() => SetAccount(password: null, isDisabled: false);

        private void SetAccount(string? password, bool isDisabled) =>
            Repository
                .Setup(r => r.GetAuthByIdAsync(UserId))
                .ReturnsAsync(new UserAuthRecord
                {
                    Id = UserId,
                    Email = CurrentEmail,
                    Password = password,
                    Usertype = "user",
                    Name = "Ada",
                    IsDisabled = isDisabled,
                    AuthVersion = 1,
                });

        public void WithRegisteredAddress(string email) =>
            Availability
                .Setup(a => a.IsRegisteredAsync(
                    email,
                    It.IsAny<AvailabilityLookupMode>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        public void WithSuccessfulChange() =>
            Repository
                .Setup(r => r.ChangeEmailAsync(UserId, NewEmail, It.IsAny<DateTime>()))
                .ReturnsAsync(new EmailChangeRecord(
                    EmailChangeStatus.Changed,
                    new User { Id = UserId, Email = NewEmail, Usertype = "user" },
                    PreviousEmail: CurrentEmail));
    }
}
