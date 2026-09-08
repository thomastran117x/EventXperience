using backend.main.features.auth.token;

namespace backend.main.features.profile.email
{
    /// <summary>
    /// Moves an account to a new email address, which is only ever done against an address whose
    /// owner has proved they can read it. The address is a sign-in identity and an access token
    /// claim, so the change is deliberately split in two: a request that proves the caller is the
    /// account holder, and a confirmation that proves they hold the new inbox.
    /// </summary>
    public interface IEmailChangeService
    {
        /// <summary>
        /// Validates the requested address and sends the confirmation to it, plus a heads-up to
        /// the address being replaced. Nothing about the account changes until the confirmation
        /// comes back.
        /// </summary>
        /// <returns>The OTP challenge, so the caller can confirm without leaving the page.</returns>
        Task<VerificationOtpChallenge> RequestChangeAsync(
            int userId,
            string newEmail,
            string? currentPassword,
            CancellationToken cancellationToken = default);

        /// <summary>Applies a change whose new address has been proved. Revokes every session.</summary>
        Task ConfirmAsync(PendingEmailChange pending, CancellationToken cancellationToken = default);

        Task<PendingEmailChange?> GetPendingAsync(int userId);

        Task CancelPendingAsync(int userId);
    }
}
