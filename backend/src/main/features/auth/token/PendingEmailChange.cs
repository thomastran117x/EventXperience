namespace backend.main.features.auth.token
{
    /// <summary>
    /// A verified email-change request read back out of the verification store. Unlike the signup
    /// and password-reset purposes, which reconstitute a whole <c>User</c>, an email change only
    /// needs to know which account asked and which address it asked for.
    /// </summary>
    public sealed record PendingEmailChange(int UserId, string NewEmail, DateTime ExpiresAtUtc);
}
