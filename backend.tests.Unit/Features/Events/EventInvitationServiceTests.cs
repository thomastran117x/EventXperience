using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using backend.main.features.cache;
using backend.main.features.clubs;
using backend.main.features.events;
using backend.main.features.events.images;
using backend.main.features.events.invitations;
using backend.main.features.events.invitations.contracts.responses;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;
using backend.main.shared.exceptions.http;
using backend.main.shared.providers;
using backend.main.shared.providers.messages;

using FluentAssertions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Moq;

namespace backend.tests.Unit.Features.Events;

public class EventInvitationServiceTests
{
    [Fact]
    public async Task HasAcceptedInvitationAccessAsync_ShouldReturnTrueOnlyForAcceptedInvitations()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        harness.Db.EventInvitations.AddRange(
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Accepted),
            harness.BuildInvitation(ev.Id, harness.OtherUserId, lifecycleStatus: EventInvitationLifecycleStatus.Pending));
        await harness.Db.SaveChangesAsync();

        var accepted = await harness.Service.HasAcceptedInvitationAccessAsync(ev.Id, harness.MemberUserId);
        var pending = await harness.Service.HasAcceptedInvitationAccessAsync(ev.Id, harness.OtherUserId);

        accepted.Should().BeTrue();
        pending.Should().BeFalse();
    }

    [Fact]
    public async Task CreateInvitationsAsync_ShouldCreateDirectInvites_PublishEmails_AndInvalidateCache()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync(name: "Secret Mixer");
        var publishedMessages = new List<EmailMessage>();

        harness.UserRepositoryMock
            .Setup(repo => repo.GetUserAsync(harness.MemberUserId))
            .ReturnsAsync(new User
            {
                Id = harness.MemberUserId,
                Email = "member@test.local",
                Usertype = "Participant"
            });
        harness.PublisherMock
            .Setup(publisher => publisher.PublishAsync(It.IsAny<string>(), It.IsAny<EmailMessage>()))
            .Callback<string, EmailMessage>((_, message) => publishedMessages.Add(message))
            .Returns(Task.CompletedTask);

        var response = await harness.Service.CreateInvitationsAsync(
            ev.Id,
            harness.OwnerUserId,
            harness.OwnerRole,
            [harness.MemberUserId],
            ["guest@test.local", " GUEST@test.local "],
            harness.Time.Now.UtcDateTime.AddDays(2));

        response.Should().HaveCount(2);
        response.Select(item => item.SourceType).Should().Contain(["DirectUser", "DirectEmail"]);
        publishedMessages.Should().HaveCount(2);
        publishedMessages.Should().OnlyContain(message =>
            message.Type == EmailMessageType.EventInvite &&
            message.EventName == ev.Name &&
            !string.IsNullOrWhiteSpace(message.Token));

        var persisted = await harness.Db.EventInvitations
            .OrderBy(item => item.Id)
            .ToListAsync();
        persisted.Should().HaveCount(2);
        persisted.Should().ContainSingle(item => item.SourceType == EventInvitationSource.DirectUser && item.RecipientUserId == harness.MemberUserId);
        persisted.Should().ContainSingle(item => item.SourceType == EventInvitationSource.DirectEmail && item.RecipientEmailNormalized == "guest@test.local");

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:list"), Times.Once);
    }

    [Fact]
    public async Task CreateInvitationsAsync_ShouldReuseExistingPendingInvitations()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        harness.UserRepositoryMock
            .Setup(repo => repo.GetUserAsync(harness.MemberUserId))
            .ReturnsAsync(new User
            {
                Id = harness.MemberUserId,
                Email = "member@test.local",
                Usertype = "Participant"
            });

        harness.Db.EventInvitations.AddRange(
            harness.BuildInvitation(
                ev.Id,
                harness.MemberUserId,
                sourceType: EventInvitationSource.DirectUser,
                lifecycleStatus: EventInvitationLifecycleStatus.Pending,
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(1)),
            harness.BuildInvitation(
                ev.Id,
                null,
                recipientEmail: "guest@test.local",
                sourceType: EventInvitationSource.DirectEmail,
                lifecycleStatus: EventInvitationLifecycleStatus.Pending,
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(1)));
        await harness.Db.SaveChangesAsync();

        var response = await harness.Service.CreateInvitationsAsync(
            ev.Id,
            harness.OwnerUserId,
            harness.OwnerRole,
            [harness.MemberUserId],
            ["guest@test.local"],
            harness.Time.Now.UtcDateTime.AddDays(2));

        response.Should().HaveCount(2);
        (await harness.Db.EventInvitations.CountAsync()).Should().Be(2);
        harness.PublisherMock.Verify(publisher => publisher.PublishAsync(It.IsAny<string>(), It.IsAny<EmailMessage>()), Times.Never);
    }

    [Fact]
    public async Task CreateInvitationLinkAsync_ShouldPersistLink_ReturnShareUrl_AndInvalidateCache()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();

        var response = await harness.Service.CreateInvitationLinkAsync(
            ev.Id,
            harness.OwnerUserId,
            harness.OwnerRole,
            maxRedemptions: 3,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(3));

        response.Id.Should().BeGreaterThan(0);
        response.EventId.Should().Be(ev.Id);
        response.ShareUrl.Should().StartWith("/events/invite?token=");
        response.MaxRedemptions.Should().Be(3);
        response.RedemptionCount.Should().Be(0);

        var persisted = await harness.Db.EventInvitationLinks.SingleAsync();
        persisted.TokenHash.Should().NotBeNullOrWhiteSpace();
        persisted.TokenHash.Should().NotContain("token=");

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:links"), Times.Once);
    }

    [Fact]
    public async Task ResolveInvitationAsync_ShouldRequireLogin_ForPendingDirectInvitation()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync(name: "Invite Only Night");
        var token = "direct-token";
        harness.Db.EventInvitations.Add(
            harness.BuildInvitation(
                ev.Id,
                recipientUserId: harness.MemberUserId,
                sourceType: EventInvitationSource.DirectUser,
                lifecycleStatus: EventInvitationLifecycleStatus.Pending,
                token: token,
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(2)));
        await harness.Db.SaveChangesAsync();

        var resolved = await harness.Service.ResolveInvitationAsync(token);

        resolved.State.Should().Be(EventInvitationResolveState.LoginRequired.ToString());
        resolved.RequiresAuthentication.Should().BeTrue();
        resolved.CanAccept.Should().BeTrue();
        resolved.CanDecline.Should().BeFalse();
        resolved.Event.Should().NotBeNull();
        resolved.Event!.Name.Should().Be("Invite Only Night");
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldAcceptDirectInvitation_AndInvalidateCaches()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var token = "accept-direct";
        var invitation = harness.BuildInvitation(
            ev.Id,
            recipientUserId: null,
            recipientEmail: "guest@test.local",
            sourceType: EventInvitationSource.DirectEmail,
            lifecycleStatus: EventInvitationLifecycleStatus.Pending,
            token: token,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(2));
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var decision = await harness.Service.AcceptInvitationAsync(token, harness.MemberUserId, "Guest@Test.Local");

        decision.Invitation.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted.ToString());
        decision.Invitation.Event.Should().NotBeNull();

        var persisted = await harness.Db.EventInvitations.SingleAsync(item => item.Id == invitation.Id);
        persisted.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted);
        persisted.AcceptedByUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientEmailNormalized.Should().Be("guest@test.local");

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync("invitation:user:9:guest@test.local"), Times.Once);
        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:list"), Times.Once);
    }

    [Fact]
    public async Task AcceptInvitationAsync_ShouldCreateAcceptedLinkClaim_AndIncrementRedemptions()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var token = "link-accept";
        var link = harness.BuildLink(
            ev.Id,
            token,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(2),
            maxRedemptions: 2);
        harness.Db.EventInvitationLinks.Add(link);
        await harness.Db.SaveChangesAsync();

        var decision = await harness.Service.AcceptInvitationAsync(token, harness.MemberUserId, "member@test.local");

        decision.Invitation.SourceType.Should().Be(EventInvitationSource.LinkClaim.ToString());
        decision.Invitation.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted.ToString());

        var persistedLink = await harness.Db.EventInvitationLinks.SingleAsync(item => item.Id == link.Id);
        persistedLink.RedemptionCount.Should().Be(1);
        var claim = await harness.Db.EventInvitations.SingleAsync(item => item.EventInvitationLinkId == link.Id);
        claim.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted);
        claim.SourceType.Should().Be(EventInvitationSource.LinkClaim);
    }

    [Fact]
    public async Task DeclineInvitationAsync_ShouldCreateDeclinedLinkClaim()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var token = "link-decline";
        var link = harness.BuildLink(
            ev.Id,
            token,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(2),
            maxRedemptions: 2);
        harness.Db.EventInvitationLinks.Add(link);
        await harness.Db.SaveChangesAsync();

        var decision = await harness.Service.DeclineInvitationAsync(token, harness.MemberUserId, "member@test.local");

        decision.Invitation.SourceType.Should().Be(EventInvitationSource.LinkClaim.ToString());
        decision.Invitation.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Declined.ToString());

        var claim = await harness.Db.EventInvitations.SingleAsync(item => item.EventInvitationLinkId == link.Id);
        claim.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Declined);
        (await harness.Db.EventInvitationLinks.SingleAsync(item => item.Id == link.Id)).RedemptionCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMyInvitationsAsync_ShouldFilterToPendingAndAcceptedInvitations()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        harness.SetupMyInvitationsCachePassThrough(harness.MemberUserId, "member@test.local");

        harness.Db.EventInvitations.AddRange(
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Pending, createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-1)),
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Accepted, createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-2)),
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Declined, createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-3)),
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Revoked, createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-4)),
            harness.BuildInvitation(ev.Id, harness.MemberUserId, lifecycleStatus: EventInvitationLifecycleStatus.Pending, expiresAt: harness.Time.Now.UtcDateTime.AddMinutes(-1), createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-5)));
        await harness.Db.SaveChangesAsync();

        var invitations = await harness.Service.GetMyInvitationsAsync(harness.MemberUserId, "member@test.local");

        invitations.Should().HaveCount(2);
        invitations.Select(item => item.EffectiveStatus).Should().Equal(
            EventInvitationEffectiveStatus.Pending.ToString(),
            EventInvitationEffectiveStatus.Accepted.ToString());
        invitations.Should().OnlyContain(item => item.Event != null);
    }

    [Fact]
    public async Task MarkInvitationDeliveryStatusAsync_ShouldTrimError_AndIgnoreMissingInvitation()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(ev.Id, harness.MemberUserId);
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        await harness.Service.MarkInvitationDeliveryStatusAsync(invitation.Id, EventInvitationDeliveryStatus.Failed, "  mailbox full  ");
        await harness.Service.MarkInvitationDeliveryStatusAsync(999, EventInvitationDeliveryStatus.Sent, null);

        var persisted = await harness.Db.EventInvitations.SingleAsync(item => item.Id == invitation.Id);
        persisted.DeliveryStatus.Should().Be(EventInvitationDeliveryStatus.Failed);
        persisted.DeliveryError.Should().Be("mailbox full");
    }

    [Fact]
    public async Task GetEventInvitationsAsync_ShouldReturnNewestFirst_AndIncludeEventSummary()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync(name: "Shadow Summit");
        harness.SetupEventInvitationsCachePassThrough(ev.Id);

        harness.Db.EventInvitations.AddRange(
            harness.BuildInvitation(
                ev.Id,
                recipientUserId: harness.MemberUserId,
                createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-10)),
            harness.BuildInvitation(
                ev.Id,
                recipientUserId: null,
                recipientEmail: "latest@test.local",
                sourceType: EventInvitationSource.DirectEmail,
                createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-1)));
        await harness.Db.SaveChangesAsync();

        var invitations = await harness.Service.GetEventInvitationsAsync(ev.Id, harness.OwnerUserId, harness.OwnerRole);

        invitations.Should().HaveCount(2);
        invitations.Select(item => item.RecipientEmail).Should().ContainInOrder("latest@test.local", $"user{harness.MemberUserId}@test.local");
        invitations[0].Event.Should().NotBeNull();
        invitations[0].Event!.Name.Should().Be("Shadow Summit");
    }

    [Fact]
    public async Task RevokeInvitationAsync_ShouldMarkInvitationRevoked_AndInvalidateCache()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(ev.Id, harness.MemberUserId, createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-5));
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var revoked = await harness.Service.RevokeInvitationAsync(ev.Id, invitation.Id, harness.OwnerUserId, harness.OwnerRole);

        revoked.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Revoked.ToString());
        revoked.RevokedAtUtc.Should().Be(harness.Time.Now.UtcDateTime);

        var persisted = await harness.Db.EventInvitations.SingleAsync(item => item.Id == invitation.Id);
        persisted.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Revoked);
        persisted.RevokedByUserId.Should().Be(harness.OwnerUserId);
        persisted.RevokedAtUtc.Should().Be(harness.Time.Now.UtcDateTime);

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:list"), Times.Once);
    }

    [Fact]
    public async Task CreateInvitationsAsync_ShouldRejectPublicEvents()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedEventAsync(isPrivate: false);

        var action = () => harness.Service.CreateInvitationsAsync(
            ev.Id,
            harness.OwnerUserId,
            harness.OwnerRole,
            [harness.MemberUserId],
            [],
            harness.Time.Now.UtcDateTime.AddDays(1));

        await action.Should()
            .ThrowAsync<BadRequestException>()
            .WithMessage("Invitations are only supported for private events.");
    }

    [Fact]
    public async Task CreateInvitationsAsync_ShouldRejectUnpublishedPrivateEvents()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedEventAsync(lifecycleState: EventLifecycleState.Draft);

        var action = () => harness.Service.CreateInvitationsAsync(
            ev.Id,
            harness.OwnerUserId,
            harness.OwnerRole,
            [harness.MemberUserId],
            [],
            harness.Time.Now.UtcDateTime.AddDays(1));

        await action.Should()
            .ThrowAsync<BadRequestException>()
            .WithMessage("Invitations are only supported for published private events.");
    }

    [Fact]
    public async Task GetInvitationLinksAsync_ShouldReturnNewestFirst_WithoutRawShareUrls()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        harness.SetupInvitationLinksCachePassThrough(ev.Id);

        harness.Db.EventInvitationLinks.AddRange(
            harness.BuildLink(
                ev.Id,
                token: "older-link",
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(2),
                createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-20)),
            harness.BuildLink(
                ev.Id,
                token: "newer-link",
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(3),
                createdAt: harness.Time.Now.UtcDateTime.AddMinutes(-2)));
        await harness.Db.SaveChangesAsync();

        var links = await harness.Service.GetInvitationLinksAsync(ev.Id, harness.OwnerUserId, harness.OwnerRole);

        links.Should().HaveCount(2);
        links.Select(item => item.CreatedAt).Should().BeInDescendingOrder();
        links.Should().OnlyContain(item => item.ShareUrl == null);
    }

    [Fact]
    public async Task RevokeInvitationLinkAsync_ShouldMarkLinkRevoked_AndInvalidateCache()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var link = harness.BuildLink(ev.Id, "revoke-link", harness.Time.Now.UtcDateTime.AddDays(4));
        harness.Db.EventInvitationLinks.Add(link);
        await harness.Db.SaveChangesAsync();

        var revoked = await harness.Service.RevokeInvitationLinkAsync(ev.Id, link.Id, harness.OwnerUserId, harness.OwnerRole);

        revoked.IsRevoked.Should().BeTrue();
        revoked.RevokedAtUtc.Should().Be(harness.Time.Now.UtcDateTime);

        var persisted = await harness.Db.EventInvitationLinks.SingleAsync(item => item.Id == link.Id);
        persisted.RevokedByUserId.Should().Be(harness.OwnerUserId);
        persisted.RevokedAtUtc.Should().Be(harness.Time.Now.UtcDateTime);

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:links"), Times.Once);
    }

    [Fact]
    public async Task ResolveInvitationAsync_ShouldReturnInvalid_ForMismatchedDirectRecipient()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var token = "wrong-user-token";
        harness.Db.EventInvitations.Add(
            harness.BuildInvitation(
                ev.Id,
                recipientUserId: harness.MemberUserId,
                sourceType: EventInvitationSource.DirectUser,
                token: token,
                expiresAt: harness.Time.Now.UtcDateTime.AddDays(2)));
        await harness.Db.SaveChangesAsync();

        var resolved = await harness.Service.ResolveInvitationAsync(token, harness.OtherUserId, "other@test.local");

        resolved.State.Should().Be(EventInvitationResolveState.Invalid.ToString());
        resolved.CanAccept.Should().BeFalse();
        resolved.CanDecline.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveInvitationAsync_ShouldReturnAlreadyAccepted_ForExistingLinkClaim()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var token = "existing-link-token";
        var link = harness.BuildLink(ev.Id, token, harness.Time.Now.UtcDateTime.AddDays(2), maxRedemptions: 3);
        harness.Db.EventInvitationLinks.Add(link);
        await harness.Db.SaveChangesAsync();

        harness.Db.EventInvitations.Add(new EventInvitation
        {
            EventId = ev.Id,
            RecipientUserId = harness.MemberUserId,
            RecipientEmail = "member@test.local",
            RecipientEmailNormalized = "member@test.local",
            SourceType = EventInvitationSource.LinkClaim,
            LifecycleStatus = EventInvitationLifecycleStatus.Accepted,
            DeliveryStatus = EventInvitationDeliveryStatus.Skipped,
            EventInvitationLinkId = link.Id,
            AcceptedAtUtc = harness.Time.Now.UtcDateTime.AddMinutes(-5),
            AcceptedByUserId = harness.MemberUserId,
            CreatedByUserId = harness.OwnerUserId,
            CreatedAt = harness.Time.Now.UtcDateTime.AddMinutes(-5),
            UpdatedAt = harness.Time.Now.UtcDateTime.AddMinutes(-5)
        });
        await harness.Db.SaveChangesAsync();

        var resolved = await harness.Service.ResolveInvitationAsync(token, harness.MemberUserId, "member@test.local");

        resolved.State.Should().Be(EventInvitationResolveState.AlreadyAccepted.ToString());
        resolved.SourceType.Should().Be(EventInvitationSource.LinkClaim.ToString());
        resolved.CanAccept.Should().BeFalse();
        resolved.CanDecline.Should().BeFalse();
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldAcceptPendingDirectInvitation_AndInvalidateCaches()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(
            ev.Id,
            recipientUserId: null,
            recipientEmail: "invitee@test.local",
            sourceType: EventInvitationSource.DirectEmail,
            lifecycleStatus: EventInvitationLifecycleStatus.Pending,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(2));
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var decision = await harness.Service.AcceptInvitationByIdAsync(invitation.Id, harness.MemberUserId, "Invitee@Test.Local");

        decision.Invitation.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted.ToString());

        var persisted = await harness.Db.EventInvitations.SingleAsync(item => item.Id == invitation.Id);
        persisted.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Accepted);
        persisted.AcceptedByUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientEmailNormalized.Should().Be("invitee@test.local");

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync("invitation:user:9:invitee@test.local"), Times.Once);
        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:list"), Times.Once);
    }

    [Fact]
    public async Task AcceptInvitationByIdAsync_ShouldRejectLinkBasedInvitations()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(
            ev.Id,
            recipientUserId: harness.MemberUserId,
            sourceType: EventInvitationSource.LinkClaim,
            lifecycleStatus: EventInvitationLifecycleStatus.Pending);
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var action = () => harness.Service.AcceptInvitationByIdAsync(invitation.Id, harness.MemberUserId, "member@test.local");

        await action.Should()
            .ThrowAsync<BadRequestException>()
            .WithMessage("Link-based invitations must be accepted from the invitation link.");
    }

    [Fact]
    public async Task DeclineInvitationByIdAsync_ShouldDeclinePendingDirectInvitation_AndInvalidateCaches()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(
            ev.Id,
            recipientUserId: null,
            recipientEmail: "invitee@test.local",
            sourceType: EventInvitationSource.DirectEmail,
            lifecycleStatus: EventInvitationLifecycleStatus.Pending,
            expiresAt: harness.Time.Now.UtcDateTime.AddDays(2));
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var decision = await harness.Service.DeclineInvitationByIdAsync(invitation.Id, harness.MemberUserId, "Invitee@Test.Local");

        decision.Invitation.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Declined.ToString());

        var persisted = await harness.Db.EventInvitations.SingleAsync(item => item.Id == invitation.Id);
        persisted.LifecycleStatus.Should().Be(EventInvitationLifecycleStatus.Declined);
        persisted.DeclinedByUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientUserId.Should().Be(harness.MemberUserId);
        persisted.RecipientEmailNormalized.Should().Be("invitee@test.local");

        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync("invitation:user:9:invitee@test.local"), Times.Once);
        harness.RefreshCacheMock.Verify(cache => cache.RemoveAsync($"invitation:event:{ev.Id}:list"), Times.Once);
    }

    [Fact]
    public async Task DeclineInvitationByIdAsync_ShouldRejectLinkBasedInvitations()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();
        var invitation = harness.BuildInvitation(
            ev.Id,
            recipientUserId: harness.MemberUserId,
            sourceType: EventInvitationSource.LinkClaim,
            lifecycleStatus: EventInvitationLifecycleStatus.Pending);
        harness.Db.EventInvitations.Add(invitation);
        await harness.Db.SaveChangesAsync();

        var action = () => harness.Service.DeclineInvitationByIdAsync(invitation.Id, harness.MemberUserId, "member@test.local");

        await action.Should()
            .ThrowAsync<BadRequestException>()
            .WithMessage("Link-based invitations must be declined from the invitation link.");
    }

    /// <summary>
    /// An unclaimed invitation is matched by address, so an account that changes its email would
    /// otherwise lose sight of everything sent to the address it left. Binding the row to the
    /// account id fixes it for this change and every later one.
    /// </summary>
    [Fact]
    public async Task RelinkForEmailChangeAsync_ShouldClaimInvitationsForBothAddresses()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();

        var toOldAddress = harness.BuildInvitation(ev.Id, null, recipientEmail: "old@test.local");
        var toNewAddress = harness.BuildInvitation(ev.Id, null, recipientEmail: "new@test.local");
        var toSomeoneElse = harness.BuildInvitation(ev.Id, null, recipientEmail: "other@test.local");
        harness.Db.EventInvitations.AddRange(toOldAddress, toNewAddress, toSomeoneElse);
        await harness.Db.SaveChangesAsync();

        await harness.Service.RelinkForEmailChangeAsync(
            harness.MemberUserId,
            "old@test.local",
            "new@test.local");

        harness.Db.ChangeTracker.Clear();
        var invitations = await harness.Db.EventInvitations.ToListAsync();

        invitations.Single(i => i.Id == toOldAddress.Id).RecipientUserId
            .Should().Be(harness.MemberUserId);
        invitations.Single(i => i.Id == toNewAddress.Id).RecipientUserId
            .Should().Be(harness.MemberUserId);
        invitations.Single(i => i.Id == toSomeoneElse.Id).RecipientUserId
            .Should().BeNull();
    }

    [Fact]
    public async Task RelinkForEmailChangeAsync_ShouldLeaveInvitationsThatAlreadyHaveARecipient()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();
        var ev = await harness.SeedPrivatePublishedEventAsync();

        var someoneElses = harness.BuildInvitation(
            ev.Id,
            harness.OtherUserId,
            recipientEmail: "old@test.local");
        harness.Db.EventInvitations.Add(someoneElses);
        await harness.Db.SaveChangesAsync();

        await harness.Service.RelinkForEmailChangeAsync(
            harness.MemberUserId,
            "old@test.local",
            "new@test.local");

        harness.Db.ChangeTracker.Clear();
        (await harness.Db.EventInvitations.SingleAsync(i => i.Id == someoneElses.Id))
            .RecipientUserId.Should().Be(harness.OtherUserId);
    }

    /// <summary>
    /// The my-invitations cache is keyed by address, so both keys have to go or the user keeps
    /// seeing a list built for an address they no longer have.
    /// </summary>
    [Fact]
    public async Task RelinkForEmailChangeAsync_ShouldEvictBothCacheKeys()
    {
        await using var harness = await EventInvitationHarness.CreateAsync();

        await harness.Service.RelinkForEmailChangeAsync(
            harness.MemberUserId,
            "old@test.local",
            "new@test.local");

        harness.RefreshCacheMock.Verify(
            cache => cache.RemoveAsync($"invitation:user:{harness.MemberUserId}:old@test.local"),
            Times.Once);
        harness.RefreshCacheMock.Verify(
            cache => cache.RemoveAsync($"invitation:user:{harness.MemberUserId}:new@test.local"),
            Times.Once);
    }

    private sealed class EventInvitationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDatabaseContext Db { get; }
        public EventInvitationService Service { get; }
        public Mock<IClubService> ClubServiceMock { get; } = new();
        public Mock<IUserRepository> UserRepositoryMock { get; } = new();
        public Mock<IPublisher> PublisherMock { get; } = new();
        public Mock<IRefreshAheadCache> RefreshCacheMock { get; } = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 6, 4, 18, 30, 0, TimeSpan.Zero));

        public int ClubId => 4;
        public int OwnerUserId => 7;
        public int MemberUserId => 9;
        public int OtherUserId => 10;
        public string OwnerRole => "Organizer";

        private EventInvitationHarness(SqliteConnection connection, AppDatabaseContext db)
        {
            _connection = connection;
            Db = db;

            ClubServiceMock
                .Setup(service => service.CanManageClubAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
                .ReturnsAsync((int clubId, int userId, string? _) => clubId == ClubId && userId == OwnerUserId);

            PublisherMock
                .Setup(publisher => publisher.PublishAsync(It.IsAny<string>(), It.IsAny<EmailMessage>()))
                .Returns(Task.CompletedTask);

            RefreshCacheMock
                .Setup(cache => cache.RemoveAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            Service = new EventInvitationService(
                db,
                ClubServiceMock.Object,
                UserRepositoryMock.Object,
                PublisherMock.Object,
                Time,
                RefreshCacheMock.Object);
        }

        public static async Task<EventInvitationHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDatabaseContext>()
                .UseSqlite(connection)
                .Options;

            var db = new AppDatabaseContext(options);
            await db.Database.EnsureCreatedAsync();

            db.Users.AddRange(
                new User
                {
                    Id = 7,
                    Email = "owner@test.local",
                    Usertype = "Organizer"
                },
                new User
                {
                    Id = 9,
                    Email = "member@test.local",
                    Usertype = "Participant"
                },
                new User
                {
                    Id = 10,
                    Email = "other@test.local",
                    Usertype = "Participant"
                });
            await db.SaveChangesAsync();

            db.Clubs.Add(new Club
            {
                Id = 4,
                UserId = 7,
                Name = "Invite Club",
                Description = "A private club for invitation tests.",
                Clubtype = ClubType.Gaming,
                ClubImage = "https://cdn.test/clubs/invite.png"
            });
            await db.SaveChangesAsync();

            return new EventInvitationHarness(connection, db);
        }

        public async Task<backend.main.features.events.Events> SeedPrivatePublishedEventAsync(
            int id = 21,
            string name = "Private Event")
            => await SeedEventAsync(id, name, isPrivate: true, lifecycleState: EventLifecycleState.Published);

        public async Task<backend.main.features.events.Events> SeedEventAsync(
            int id = 21,
            string name = "Private Event",
            bool isPrivate = true,
            EventLifecycleState lifecycleState = EventLifecycleState.Published)
        {
            var ev = new backend.main.features.events.Events
            {
                Id = id,
                ClubId = ClubId,
                Name = name,
                Description = "An invitation-only event.",
                Location = "Secret Hall",
                LifecycleState = lifecycleState,
                isPrivate = isPrivate,
                maxParticipants = 50,
                registerCost = 0,
                Category = EventCategory.Gaming,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(2)
            };
            Db.Events.Add(ev);
            await Db.SaveChangesAsync();

            Db.EventImages.Add(new EventImage
            {
                EventId = ev.Id,
                ImageUrl = "https://cdn.test/events/private.png",
                SortOrder = 0
            });
            await Db.SaveChangesAsync();
            return ev;
        }

        public EventInvitation BuildInvitation(
            int eventId,
            int? recipientUserId,
            string? recipientEmail = null,
            EventInvitationSource sourceType = EventInvitationSource.DirectUser,
            EventInvitationLifecycleStatus lifecycleStatus = EventInvitationLifecycleStatus.Pending,
            string? token = null,
            DateTime? expiresAt = null,
            DateTime? createdAt = null)
        {
            var now = createdAt ?? Time.Now.UtcDateTime;
            var email = recipientEmail ?? (recipientUserId.HasValue ? $"user{recipientUserId.Value}@test.local" : null);

            return new EventInvitation
            {
                EventId = eventId,
                RecipientUserId = recipientUserId,
                RecipientEmail = email,
                RecipientEmailNormalized = email == null ? null : NormalizeEmail(email),
                SourceType = sourceType,
                LifecycleStatus = lifecycleStatus,
                DeliveryStatus = EventInvitationDeliveryStatus.Queued,
                ClaimTokenHash = token == null ? null : ComputeTokenHash(token),
                ExpiresAt = expiresAt,
                CreatedByUserId = OwnerUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        public EventInvitationLink BuildLink(
            int eventId,
            string token,
            DateTime expiresAt,
            int maxRedemptions = 1,
            DateTime? createdAt = null)
        {
            var now = createdAt ?? Time.Now.UtcDateTime;
            return new EventInvitationLink
            {
                EventId = eventId,
                TokenHash = ComputeTokenHash(token),
                ExpiresAt = expiresAt,
                MaxRedemptions = maxRedemptions,
                RedemptionCount = 0,
                CreatedByUserId = OwnerUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        public void SetupEventInvitationsCachePassThrough(int eventId)
        {
            RefreshCacheMock
                .Setup(cache => cache.GetOrSetAsync<IReadOnlyList<EventInvitationResponse>>(
                    $"invitation:event:{eventId}:list",
                    It.IsAny<Func<Task<IReadOnlyList<EventInvitationResponse>?>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<double>(),
                    It.IsAny<JsonSerializerOptions?>()))
                .Returns((string _, Func<Task<IReadOnlyList<EventInvitationResponse>?>> factory, TimeSpan _, TimeSpan? _, double _, JsonSerializerOptions? _) => factory());
        }

        public void SetupInvitationLinksCachePassThrough(int eventId)
        {
            RefreshCacheMock
                .Setup(cache => cache.GetOrSetAsync<IReadOnlyList<EventInvitationLinkResponse>>(
                    $"invitation:event:{eventId}:links",
                    It.IsAny<Func<Task<IReadOnlyList<EventInvitationLinkResponse>?>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<double>(),
                    It.IsAny<JsonSerializerOptions?>()))
                .Returns((string _, Func<Task<IReadOnlyList<EventInvitationLinkResponse>?>> factory, TimeSpan _, TimeSpan? _, double _, JsonSerializerOptions? _) => factory());
        }

        public void SetupMyInvitationsCachePassThrough(int userId, string normalizedEmail)
        {
            RefreshCacheMock
                .Setup(cache => cache.GetOrSetAsync<IReadOnlyList<EventInvitationResponse>>(
                    $"invitation:user:{userId}:{normalizedEmail}",
                    It.IsAny<Func<Task<IReadOnlyList<EventInvitationResponse>?>>>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<double>(),
                    It.IsAny<JsonSerializerOptions?>()))
                .Returns((string _, Func<Task<IReadOnlyList<EventInvitationResponse>?>> factory, TimeSpan _, TimeSpan? _, double _, JsonSerializerOptions? _) => factory());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
