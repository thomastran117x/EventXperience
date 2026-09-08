namespace backend.main.features.auth.token
{
    /// <summary>
    /// A verified email-change request read back out of the verification store. Unlike the signup
    /// and password-reset purposes, which reconstitute a whole <c>User</c>, an email change only
    /// needs to know which account asked and which address it asked for.
    /// </summary>
    /// <param name="AuthVersion">
    /// The account's auth version when the change was requested. A pending change must not
    /// outlive a credential rotation: the heads-up sent to the old address tells its owner to
    /// change their password, and that has to actually stop the change.
    /// </param>
    public sealed record PendingEmailChange(
        int UserId,
        int AuthVersion,
        string NewEmail,
        DateTime ExpiresAtUtc
    );
}
