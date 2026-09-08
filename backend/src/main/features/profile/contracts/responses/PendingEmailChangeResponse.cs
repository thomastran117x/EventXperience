namespace backend.main.features.profile.contracts.responses
{
    public sealed class PendingEmailChangeResponse
    {
        public required string NewEmail
        {
            get; init;
        }
        public DateTime ExpiresAtUtc
        {
            get; init;
        }
    }
}
