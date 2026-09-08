using backend.main.shared.providers.messages;
using backend.worker.email_worker;

namespace backend.tests.Unit.Workers.Email;

public class EmailTemplateRendererTests
{
    [Fact]
    public void Render_VerifyEmail_ProducesBrandedContentWithCodeAndLink()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "verify token/+",
            Type = EmailMessageType.VerifyEmail,
            Code = "123456",
            RecipientName = "Thomas"
        });

        Assert.Equal("Verify your email", content.Subject);
        // URL-encoded token in both bodies.
        Assert.Contains("/auth/verify?token=verify%20token%2F%2B", content.PlainText);
        Assert.Contains("/auth/verify?token=verify%20token%2F%2B", content.Html);
        // Personalized greeting.
        Assert.Contains("Hi Thomas,", content.PlainText);
        Assert.Contains("Hi Thomas,", content.Html);
        // Code + button label present.
        Assert.Contains("Verification code: 123456", content.PlainText);
        Assert.Contains("123456", content.Html);
        Assert.Contains("Verify email", content.Html);
        // Branded chrome.
        Assert.Contains("EventXperience", content.Html);
    }

    [Fact]
    public void Render_WithoutRecipientName_FallsBackToGenericGreeting()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "reset-token",
            Type = EmailMessageType.ResetPassword,
            Code = "999000"
        });

        Assert.Equal("Reset your password", content.Subject);
        Assert.Contains("Hi there,", content.Html);
        Assert.Contains("Reset password", content.Html);
    }

    [Fact]
    public void Render_UsernameReminder_EncodesUsernameInHtml()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = EmailMessageType.UsernameReminder,
            Username = "<member&user>"
        });

        Assert.Contains("<member&user>", content.PlainText);
        Assert.Contains("&lt;member&amp;user&gt;", content.Html);
        Assert.DoesNotContain("<member&user>", content.Html);
    }

    [Fact]
    public void Render_ProviderReminder_ListsConnectedProviders()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = EmailMessageType.ProviderSignInReminder,
            SignInProviders = ["Google", "Microsoft"]
        });

        Assert.Contains("Google or Microsoft", content.PlainText);
        Assert.Contains("Go to sign in", content.Html);
    }

    [Fact]
    public void Render_EventInvite_UsesFallbackTitleWhenEventNameMissing()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "invite-token",
            Type = EmailMessageType.EventInvite
        });

        Assert.Equal("You're invited to a private event", content.Subject);
        Assert.Contains("View invitation", content.Html);
        Assert.Contains("/events/invite?token=invite-token", content.PlainText);
    }

    [Fact]
    public void Render_EventInvite_UsesProvidedEventName()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "invite-token",
            Type = EmailMessageType.EventInvite,
            EventName = "Launch Party"
        });

        Assert.Equal("You're invited to Launch Party", content.Subject);
        Assert.Contains("Launch Party", content.Html);
    }

    [Fact]
    public void Render_ClubStaffInvite_UsesClubNameAndClubInviteLink()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "club-token",
            Type = EmailMessageType.ClubStaffInvite,
            ClubName = "Chess Club",
            RecipientName = "Jordan"
        });

        Assert.Equal("You're invited to join Chess Club as staff", content.Subject);
        Assert.Contains("Hi Jordan,", content.Html);
        Assert.Contains("View invitation", content.Html);
        Assert.Contains("/clubs/invite?token=club-token", content.PlainText);
        Assert.Contains("/clubs/invite?token=club-token", content.Html);
    }

    [Fact]
    public void Render_ClubStaffInvite_FallsBackWhenClubNameMissing()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "club-token",
            Type = EmailMessageType.ClubStaffInvite
        });

        Assert.Equal("You're invited to join a club as staff", content.Subject);
    }

    [Fact]
    public void Render_ClubMemberInvite_UsesClubNameAndMemberInviteLink()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "member-token",
            Type = EmailMessageType.ClubMemberInvite,
            ClubName = "Chess Club",
            RecipientName = "Jordan"
        });

        Assert.Equal("You're invited to join Chess Club", content.Subject);
        Assert.Contains("Hi Jordan,", content.Html);
        Assert.Contains("View invitation", content.Html);
        Assert.Contains("/clubs/member-invite?token=member-token", content.PlainText);
        Assert.Contains("/clubs/member-invite?token=member-token", content.Html);
    }

    [Theory]
    [InlineData(EmailMessageType.Welcome)]
    [InlineData(EmailMessageType.PasswordChanged)]
    [InlineData(EmailMessageType.InvitationAccepted)]
    [InlineData(EmailMessageType.InvitationDeclined)]
    [InlineData(EmailMessageType.EventReminder)]
    [InlineData(EmailMessageType.AccountConfirmation)]
    [InlineData(EmailMessageType.NewDevice)]
    [InlineData(EmailMessageType.ClubStaffInvite)]
    [InlineData(EmailMessageType.ClubMemberInvite)]
    public void Render_AllSupportedTypes_ProduceNonEmptyBrandedOutput(EmailMessageType type)
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            // A token is only required by token-bearing types; supply one so token-based
            // types render, tokenless types simply ignore it.
            Token = "sample-token",
            Type = type,
            Code = "123456",
            EventName = "Launch Party",
            ActorName = "Alex",
            EventStartsAtUtc = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc)
        });

        Assert.False(string.IsNullOrWhiteSpace(content.Subject));
        Assert.Contains("EventXperience", content.Html);
        Assert.Contains("<!DOCTYPE html>", content.Html);
    }

    [Fact]
    public void Render_ReminderWithStartTime_IncludesFormattedDate()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Token = "n/a",
            Type = EmailMessageType.EventReminder,
            EventName = "Launch Party",
            EventStartsAtUtc = new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc)
        });

        Assert.Contains("10 Jul 2026", content.PlainText);
        Assert.Contains("UTC", content.PlainText);
    }

    [Fact]
    public void Render_TokenBasedTypeWithoutToken_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateRenderer().Render(new EmailMessage
            {
                Email = "member@example.com",
                Type = EmailMessageType.VerifyEmail
            }));

        Assert.Contains("requires a token", exception.Message);
    }

    [Fact]
    public void Render_UnsupportedType_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CreateRenderer().Render(new EmailMessage
            {
                Email = "member@example.com",
                Token = "verify-token",
                Type = (EmailMessageType)999
            }));

        Assert.Equal("Unsupported email type '999'.", exception.Message);
    }

    [Fact]
    public void Render_WaitlistJoined_ExplainsAutoPromotion_AndDeepLinksToTheEvent()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = EmailMessageType.WaitlistJoined,
            RecipientName = "Thomas",
            EventId = 42,
            EventName = "Board Game Night"
        });

        Assert.Equal("You're on the waitlist for Board Game Night", content.Subject);
        Assert.Contains("/events/42", content.PlainText);
        Assert.Contains("/events/42", content.Html);
        Assert.Contains("registered automatically", content.PlainText);
        Assert.Contains("Hi Thomas,", content.Html);
    }

    [Fact]
    public void Render_WaitlistPromoted_AnnouncesRegistration_AndIncludesStartTime()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = EmailMessageType.WaitlistPromoted,
            RecipientName = "Thomas",
            EventId = 42,
            EventName = "Board Game Night",
            EventStartsAtUtc = new DateTime(2026, 8, 1, 18, 30, 0, DateTimeKind.Utc)
        });

        Assert.Equal("You're in — a spot opened up for Board Game Night", content.Subject);
        Assert.Contains("/events/42", content.Html);
        Assert.Contains("Saturday, 01 Aug 2026 at 18:30 UTC", content.PlainText);
        Assert.Contains("View your registration", content.Html);
    }

    [Fact]
    public void Render_WaitlistEmail_FallsBackToEventsIndex_WhenEventIdMissing()
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = EmailMessageType.WaitlistJoined,
            EventName = "Board Game Night"
        });

        Assert.Contains("https://frontend.example.com/events", content.PlainText);
        Assert.DoesNotContain("/events/0", content.PlainText);
    }

    /// <summary>
    /// The renderer's default switch arm throws on unhandled types, so adding an
    /// EmailMessageType without a template would crash the worker on the first such
    /// message. This pins every enum value to a rendering arm.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllEmailMessageTypes))]
    public void Render_ShouldSupportEveryEmailMessageType(EmailMessageType type)
    {
        var content = CreateRenderer().Render(new EmailMessage
        {
            Email = "member@example.com",
            Type = type,
            // Superset of what any arm may require.
            Token = "sample-token",
            Code = "123456",
            RecipientName = "Thomas",
            Username = "thomas-user",
            NewEmail = "member.new@example.com",
            SignInProviders = ["Google", "Microsoft"],
            EventId = 7,
            EventName = "Sample Event",
            ClubName = "Sample Club",
            ActorName = "Sample Actor",
            EventStartsAtUtc = DateTime.UtcNow.AddDays(1)
        });

        Assert.False(string.IsNullOrWhiteSpace(content.Subject));
        Assert.False(string.IsNullOrWhiteSpace(content.Html));
        Assert.False(string.IsNullOrWhiteSpace(content.PlainText));
    }

    public static TheoryData<EmailMessageType> AllEmailMessageTypes()
    {
        var data = new TheoryData<EmailMessageType>();
        foreach (var type in Enum.GetValues<EmailMessageType>())
            data.Add(type);

        return data;
    }

    private static EmailTemplateRenderer CreateRenderer() =>
        new(new EmailWorkerOptions(
            BootstrapServers: "kafka:9092",
            Topic: "eventxperience-email",
            GroupId: "email-worker-group",
            DlqTopic: "eventxperience-email-dlq",
            StatusTopic: "eventxperience-email-status",
            SmtpServer: "smtp.example.com",
            SmtpPort: 587,
            Username: "noreply@example.com",
            Password: "secret",
            FrontendBaseUrl: "https://frontend.example.com/"));
}
