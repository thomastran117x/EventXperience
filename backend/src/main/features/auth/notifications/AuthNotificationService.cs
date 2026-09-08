using backend.main.shared.providers;
using backend.main.shared.providers.messages;

namespace backend.main.features.auth.notifications
{
    public sealed class AuthNotificationService : IAuthNotificationService
    {
        private readonly IPublisher _publisher;

        public AuthNotificationService(IPublisher publisher)
        {
            _publisher = publisher;
        }

        public Task SendSignupVerificationAsync(
            string email,
            string token,
            string code,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.VerifyEmail,
                    Email = email,
                    Token = token,
                    Code = code,
                    RecipientName = recipientName
                });
        }

        public Task SendPasswordResetAsync(
            string email,
            string token,
            string code,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.ResetPassword,
                    Email = email,
                    Token = token,
                    RecipientName = recipientName,
                    Code = code
                });
        }

        public Task SendUsernameReminderAsync(
            string email,
            string username,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.UsernameReminder,
                    Email = email,
                    Username = username,
                    RecipientName = recipientName
                });
        }

        public Task SendProviderSignInReminderAsync(
            string email,
            IReadOnlyList<string> providers,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.ProviderSignInReminder,
                    Email = email,
                    SignInProviders = providers,
                    RecipientName = recipientName
                });
        }

        public Task SendPasswordChangedAsync(string email, string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.PasswordChanged,
                    Email = email,
                    RecipientName = recipientName
                });
        }

        public Task SendEmailChangeRequestedAsync(
            string currentEmail,
            string newEmail,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.EmailChangeRequested,
                    Email = currentEmail,
                    NewEmail = newEmail,
                    RecipientName = recipientName
                });
        }

        public Task SendEmailChangeVerificationAsync(
            string newEmail,
            string token,
            string code,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.EmailChangeVerify,
                    Email = newEmail,
                    NewEmail = newEmail,
                    Token = token,
                    Code = code,
                    RecipientName = recipientName
                });
        }

        public Task SendEmailChangedAsync(
            string email,
            string newEmail,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.EmailChanged,
                    Email = email,
                    NewEmail = newEmail,
                    RecipientName = recipientName
                });
        }

        public Task SendDeviceVerificationAsync(
            string email,
            string token,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.NewDevice,
                    Email = email,
                    Token = token,
                    RecipientName = recipientName
                });
        }

        public Task SendSmsMfaAsync(
            string phoneNumber,
            string code,
            string challenge,
            DateTime expiresAtUtc,
            string purpose)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Sms,
                new SmsMfaMessage
                {
                    PhoneNumber = phoneNumber,
                    Code = code,
                    Challenge = challenge,
                    ExpiresAtUtc = expiresAtUtc,
                    Purpose = purpose
                });
        }

        public Task SendEmailMfaCodeAsync(
            string email,
            string code,
            string? recipientName = null)
        {
            return _publisher.PublishAsync(
                NotificationTopics.Email,
                new EmailMessage
                {
                    Type = EmailMessageType.MfaCode,
                    Email = email,
                    Code = code,
                    RecipientName = recipientName
                });
        }
    }
}
