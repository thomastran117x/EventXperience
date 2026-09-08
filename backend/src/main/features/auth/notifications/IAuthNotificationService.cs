namespace backend.main.features.auth.notifications
{
    public interface IAuthNotificationService
    {
        Task SendSignupVerificationAsync(
            string email,
            string token,
            string code,
            string? recipientName = null);

        Task SendPasswordResetAsync(
            string email,
            string token,
            string code,
            string? recipientName = null);

        Task SendUsernameReminderAsync(
            string email,
            string username,
            string? recipientName = null);

        Task SendProviderSignInReminderAsync(
            string email,
            IReadOnlyList<string> providers,
            string? recipientName = null);

        Task SendPasswordChangedAsync(string email, string? recipientName = null);

        /// <summary>Heads-up to the address being moved away from. Deliberately carries no link.</summary>
        Task SendEmailChangeRequestedAsync(
            string currentEmail,
            string newEmail,
            string? recipientName = null);

        /// <summary>Confirmation link and code, sent to the address being moved to.</summary>
        Task SendEmailChangeVerificationAsync(
            string newEmail,
            string token,
            string code,
            string? recipientName = null);

        /// <summary>Notice that the change has been applied, sent to both addresses.</summary>
        Task SendEmailChangedAsync(
            string email,
            string newEmail,
            string? recipientName = null);

        Task SendDeviceVerificationAsync(
            string email,
            string token,
            string? recipientName = null);

        Task SendSmsMfaAsync(
            string phoneNumber,
            string code,
            string challenge,
            DateTime expiresAtUtc,
            string purpose);

        Task SendEmailMfaCodeAsync(
            string email,
            string code,
            string? recipientName = null);
    }
}
