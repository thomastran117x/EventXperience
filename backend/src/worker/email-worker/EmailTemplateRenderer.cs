using System.Globalization;
using System.Text;

using backend.main.shared.providers.messages;

namespace backend.worker.email_worker;

/// <summary>
/// Composes branded subject/plain-text/HTML content for every
/// <see cref="EmailMessageType"/>. HTML is wrapped in the shared
/// <see cref="EmailLayout"/> so all message types share one consistent look.
/// </summary>
public sealed class EmailTemplateRenderer : IEmailContentRenderer
{
    private readonly EmailWorkerOptions _options;

    public EmailTemplateRenderer(EmailWorkerOptions options)
    {
        _options = options;
    }

    public EmailContent Render(EmailMessage message)
    {
        var content = BuildContent(message);
        return new EmailContent(content.Subject, RenderPlainText(content), RenderHtml(content));
    }

    private Content BuildContent(EmailMessage message)
    {
        var baseUrl = _options.FrontendBaseUrl.TrimEnd('/');
        var greeting = string.IsNullOrWhiteSpace(message.RecipientName)
            ? "Hi there,"
            : $"Hi {message.RecipientName!.Trim()},";
        var eventName = string.IsNullOrWhiteSpace(message.EventName) ? "a private event" : message.EventName!.Trim();
        var clubName = string.IsNullOrWhiteSpace(message.ClubName) ? "a club" : message.ClubName!.Trim();

        return message.Type switch
        {
            EmailMessageType.VerifyEmail => new Content(
                Subject: "Verify your email",
                Preheader: "Confirm your email address to finish setting up your account.",
                Heading: "Verify your email",
                Greeting: greeting,
                Intro: ["Welcome to EventXperience! Please confirm your email address to activate your account."],
                Cta: new Cta(BuildUrl(baseUrl, "/auth/verify", RequireToken(message)), "Verify email"),
                Code: message.Code,
                MutedNote: "This link will expire soon. If you didn't create an account, you can safely ignore this email."),

            EmailMessageType.AccountConfirmation => new Content(
                Subject: "Confirm your account",
                Preheader: "Confirm your EventXperience account to get started.",
                Heading: "Confirm your account",
                Greeting: greeting,
                Intro: ["Please confirm your account to finish signing up for EventXperience."],
                Cta: new Cta(BuildUrl(baseUrl, "/auth/verify", RequireToken(message)), "Confirm account"),
                Code: message.Code,
                MutedNote: "This link will expire soon. If you didn't create an account, you can safely ignore this email."),

            EmailMessageType.ResetPassword => new Content(
                Subject: "Reset your password",
                Preheader: "Use the link below to choose a new password.",
                Heading: "Reset your password",
                Greeting: greeting,
                Intro: ["We received a request to reset your password. Choose a new one using the button below."],
                Cta: new Cta(BuildUrl(baseUrl, "/auth/reset-password", RequireToken(message)), "Reset password"),
                Code: message.Code,
                MutedNote: "If you didn't request a password reset, you can safely ignore this email — your password won't change."),

            EmailMessageType.UsernameReminder => new Content(
                Subject: "Your EventXperience username",
                Preheader: "Here is the username associated with your account.",
                Heading: "Your username",
                Greeting: greeting,
                Intro: [$"The username for your EventXperience account is: {RequireUsername(message)}"],
                Cta: new Cta($"{baseUrl}/auth/login", "Sign in"),
                Code: null,
                MutedNote: "If you didn't request this reminder, you can safely ignore this email."),

            EmailMessageType.ProviderSignInReminder => new Content(
                Subject: "How to sign in to EventXperience",
                Preheader: "Use your connected provider to access your account.",
                Heading: "Sign in with your connected provider",
                Greeting: greeting,
                Intro: [$"Your account uses {ProviderLabel(message)} for sign-in. Return to EventXperience and choose that provider to continue."],
                Cta: new Cta($"{baseUrl}/auth/login", "Go to sign in"),
                Code: null,
                MutedNote: "If you didn't request account recovery, you can safely ignore this email."),

            EmailMessageType.NewDevice => new Content(
                Subject: "Confirm new device sign-in",
                Preheader: "A new device tried to sign in to your account.",
                Heading: "Confirm this device sign-in",
                Greeting: greeting,
                Intro: ["We noticed a sign-in from a new device. Confirm it was you to continue."],
                Cta: new Cta(BuildUrl(baseUrl, "/auth/device/verify", RequireToken(message)), "Verify device"),
                Code: null,
                MutedNote: "If this wasn't you, please reset your password immediately and review your account security."),

            EmailMessageType.MfaCode => new Content(
                Subject: "Your verification code",
                Preheader: "Use this code to verify it's you.",
                Heading: "Verify it's you",
                Greeting: greeting,
                Intro: ["Enter the code below to continue. It expires shortly, so use it soon."],
                Cta: null,
                Code: RequireCode(message),
                MutedNote: "If you didn't request this code, you can safely ignore this email — no action is needed."),

            EmailMessageType.EventInvite => new Content(
                Subject: $"You're invited to {eventName}",
                Preheader: $"You've been invited to {eventName} on EventXperience.",
                Heading: $"You're invited to {eventName}",
                Greeting: greeting,
                Intro: [$"You've been invited to {eventName}. View the invitation to see the details and respond."],
                Cta: new Cta(BuildUrl(baseUrl, "/events/invite", RequireToken(message)), "View invitation"),
                Code: null,
                MutedNote: "If you weren't expecting this invitation, you can safely ignore this email."),

            EmailMessageType.ClubStaffInvite => new Content(
                Subject: $"You're invited to join {clubName} as staff",
                Preheader: $"You've been invited to help run {clubName} on EventXperience.",
                Heading: $"You're invited to {clubName}",
                Greeting: greeting,
                Intro: [$"You've been invited to join the staff of {clubName}. View the invitation to accept or decline."],
                Cta: new Cta(BuildUrl(baseUrl, "/clubs/invite", RequireToken(message)), "View invitation"),
                Code: null,
                MutedNote: "If you weren't expecting this invitation, you can safely ignore this email."),

            EmailMessageType.ClubMemberInvite => new Content(
                Subject: $"You're invited to join {clubName}",
                Preheader: $"You've been invited to become a member of {clubName} on EventXperience.",
                Heading: $"You're invited to {clubName}",
                Greeting: greeting,
                Intro: [$"You've been invited to join {clubName} as a member. View the invitation to accept or decline."],
                Cta: new Cta(BuildUrl(baseUrl, "/clubs/member-invite", RequireToken(message)), "View invitation"),
                Code: null,
                MutedNote: "If you weren't expecting this invitation, you can safely ignore this email."),

            EmailMessageType.Welcome => new Content(
                Subject: "Welcome to EventXperience",
                Preheader: "Your account is ready — start exploring events.",
                Heading: "Welcome to EventXperience!",
                Greeting: greeting,
                Intro:
                [
                    "Your account is all set up. EventXperience is a modern platform for creating, managing, and scaling unforgettable event experiences.",
                    "Browse upcoming events or create your own to get started."
                ],
                Cta: new Cta($"{baseUrl}/events", "Browse events"),
                Code: null,
                MutedNote: "Need a hand? Just reply to this email and we'll be happy to help."),

            EmailMessageType.PasswordChanged => new Content(
                Subject: "Your password was changed",
                Preheader: "This is a confirmation that your password was updated.",
                Heading: "Your password was changed",
                Greeting: greeting,
                Intro: ["This is a confirmation that the password for your EventXperience account was successfully changed."],
                Cta: null,
                Code: null,
                MutedNote: "If you didn't make this change, please reset your password immediately and contact support."),

            EmailMessageType.EmailChangeRequested => new Content(
                Subject: "Confirm the change to your email address",
                Preheader: "A change to the email address on your account was requested.",
                Heading: "An email change was requested",
                Greeting: greeting,
                Intro:
                [
                    $"Someone asked to change the email address on your EventXperience account to {RequireNewEmail(message)}.",
                    "To finish the change, open the confirmation link we sent to that new address. Until then, nothing about your account has changed."
                ],
                Cta: null,
                Code: null,
                MutedNote: "If you didn't request this, you don't need to do anything - the change cannot complete without access to the new address. We'd still recommend changing your password."),

            EmailMessageType.EmailChangeVerify => new Content(
                Subject: "Confirm your new email address",
                Preheader: "Confirm this address to finish moving your account to it.",
                Heading: "Confirm your new email address",
                Greeting: greeting,
                Intro:
                [
                    "This address was given as the new email for an EventXperience account. Confirm it below to complete the change.",
                    "Once confirmed you'll be signed out everywhere and will need to sign in again using this address."
                ],
                Cta: new Cta(
                    BuildUrl(baseUrl, "/auth/verify-email-change", RequireToken(message)),
                    "Confirm email change"),
                Code: message.Code,
                MutedNote: "This link will expire soon. If you weren't expecting it, you can safely ignore this email."),

            EmailMessageType.EmailChanged => new Content(
                Subject: "Your email address was changed",
                Preheader: "This is a confirmation that your account email was updated.",
                Heading: "Your email address was changed",
                Greeting: greeting,
                Intro:
                [
                    $"The email address for your EventXperience account is now {RequireNewEmail(message)}.",
                    "Every session has been signed out. Use the new address the next time you sign in."
                ],
                Cta: new Cta($"{baseUrl}/auth/login", "Go to sign in"),
                Code: null,
                MutedNote: "If you didn't make this change, contact support immediately."),

            EmailMessageType.InvitationAccepted => new Content(
                Subject: $"{ActorLabel(message)} accepted your invitation to {eventName}",
                Preheader: $"{ActorLabel(message)} is coming to {eventName}.",
                Heading: "Invitation accepted",
                Greeting: greeting,
                Intro: [$"Good news — {ActorLabel(message)} accepted your invitation to {eventName}."],
                Cta: new Cta($"{baseUrl}/events", "View event"),
                Code: null,
                MutedNote: null),

            EmailMessageType.InvitationDeclined => new Content(
                Subject: $"{ActorLabel(message)} declined your invitation to {eventName}",
                Preheader: $"{ActorLabel(message)} won't be attending {eventName}.",
                Heading: "Invitation declined",
                Greeting: greeting,
                Intro: [$"{ActorLabel(message)} declined your invitation to {eventName}."],
                Cta: new Cta($"{baseUrl}/events", "View event"),
                Code: null,
                MutedNote: null),

            EmailMessageType.WaitlistJoined => new Content(
                Subject: $"You're on the waitlist for {eventName}",
                Preheader: $"We'll email you the moment a spot opens up at {eventName}.",
                Heading: "You're on the waitlist",
                Greeting: greeting,
                Intro:
                [
                    $"{eventName} is currently full, so we've added you to the waitlist.",
                    "If someone cancels, the next person in line is registered automatically — we'll email you right away."
                ],
                Cta: new Cta(EventUrl(baseUrl, message), "View event"),
                Code: null,
                MutedNote: "You can leave the waitlist at any time from the event page."),

            EmailMessageType.WaitlistPromoted => new Content(
                Subject: $"You're in — a spot opened up for {eventName}",
                Preheader: $"You've been moved off the waitlist and registered for {eventName}.",
                Heading: "You're registered!",
                Greeting: greeting,
                Intro:
                [
                    $"Good news — a spot opened up for {eventName} and you were next in line, so we registered you automatically.",
                    BuildReminderIntro(eventName, message.EventStartsAtUtc)
                ],
                Cta: new Cta(EventUrl(baseUrl, message), "View your registration"),
                Code: null,
                MutedNote: "Can't make it? Please unregister from the event page so the next person can take your spot."),

            EmailMessageType.EventReminder => new Content(
                Subject: $"Reminder: {eventName} is coming up",
                Preheader: $"Don't forget — {eventName} is on the way.",
                Heading: $"{eventName} is coming up",
                Greeting: greeting,
                Intro: [BuildReminderIntro(eventName, message.EventStartsAtUtc)],
                Cta: new Cta($"{baseUrl}/events", "View event"),
                Code: null,
                MutedNote: null),

            _ => throw new InvalidOperationException($"Unsupported email type '{message.Type}'.")
        };
    }

    private static string BuildReminderIntro(string eventName, DateTime? startsAtUtc)
    {
        if (startsAtUtc is null)
            return $"This is a friendly reminder that {eventName} is coming up soon.";

        var when = startsAtUtc.Value.ToUniversalTime()
            .ToString("dddd, dd MMM yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return $"This is a friendly reminder that {eventName} starts on {when}.";
    }

    private static string ActorLabel(EmailMessage message) =>
        string.IsNullOrWhiteSpace(message.ActorName) ? "A guest" : message.ActorName!.Trim();

    private static string RequireCode(EmailMessage message) =>
        string.IsNullOrWhiteSpace(message.Code)
            ? throw new InvalidOperationException($"Email type '{message.Type}' requires a code.")
            : message.Code!;

    private static string RequireToken(EmailMessage message) =>
        string.IsNullOrWhiteSpace(message.Token)
            ? throw new InvalidOperationException($"Email type '{message.Type}' requires a token.")
            : message.Token!;

    private static string RequireNewEmail(EmailMessage message) =>
        string.IsNullOrWhiteSpace(message.NewEmail)
            ? throw new InvalidOperationException($"Email type '{message.Type}' requires a new email.")
            : message.NewEmail!.Trim();

    private static string RequireUsername(EmailMessage message) =>
        string.IsNullOrWhiteSpace(message.Username)
            ? throw new InvalidOperationException($"Email type '{message.Type}' requires a username.")
            : message.Username.Trim();

    private static string ProviderLabel(EmailMessage message)
    {
        var providers = message.SignInProviders?
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(provider => provider.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return providers.Length switch
        {
            0 => "a connected provider",
            1 => providers[0],
            2 => $"{providers[0]} or {providers[1]}",
            _ => $"{string.Join(", ", providers[..^1])}, or {providers[^1]}"
        };
    }

    private static string BuildUrl(string baseUrl, string path, string token) =>
        $"{baseUrl}{path}?token={Uri.EscapeDataString(token)}";

    private static string EventUrl(string baseUrl, EmailMessage message) =>
        message.EventId is int id && id > 0 ? $"{baseUrl}/events/{id}" : $"{baseUrl}/events";

    private static string RenderHtml(Content content)
    {
        var body = new StringBuilder();
        body.Append(EmailLayout.Heading(content.Heading));
        body.Append(EmailLayout.Paragraph(content.Greeting));

        foreach (var paragraph in content.Intro)
            body.Append(EmailLayout.Paragraph(paragraph));

        if (!string.IsNullOrWhiteSpace(content.Code))
            body.Append(EmailLayout.CodeBlock(content.Code!));

        if (content.Cta is not null)
        {
            body.Append(EmailLayout.Button(content.Cta.Url, content.Cta.Label));
            body.Append(EmailLayout.LinkFallback(content.Cta.Url));
        }

        if (!string.IsNullOrWhiteSpace(content.MutedNote))
            body.Append(EmailLayout.MutedNote(content.MutedNote!));

        return EmailLayout.Document(content.Preheader, body.ToString());
    }

    private static string RenderPlainText(Content content)
    {
        var lines = new List<string> { content.Heading, string.Empty, content.Greeting, string.Empty };
        lines.AddRange(content.Intro);

        if (!string.IsNullOrWhiteSpace(content.Code))
        {
            lines.Add(string.Empty);
            lines.Add($"Verification code: {content.Code}");
        }

        if (content.Cta is not null)
        {
            lines.Add(string.Empty);
            lines.Add($"{content.Cta.Label}: {content.Cta.Url}");
        }

        if (!string.IsNullOrWhiteSpace(content.MutedNote))
        {
            lines.Add(string.Empty);
            lines.Add(content.MutedNote!);
        }

        lines.Add(string.Empty);
        lines.Add("— EventXperience");

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record Content(
        string Subject,
        string Preheader,
        string Heading,
        string Greeting,
        IReadOnlyList<string> Intro,
        Cta? Cta,
        string? Code,
        string? MutedNote);

    private sealed record Cta(string Url, string Label);
}
