namespace backend.main.shared.providers.messages
{
    public sealed class EmailMessage
    {
        public required EmailMessageType Type
        {
            get; init;
        }
        public required string Email
        {
            get; init;
        }
        public string? Token
        {
            get; init;
        }
        public string? Code
        {
            get; init;
        }
        public string? RecipientName
        {
            get; init;
        }
        public string? Username
        {
            get; init;
        }
        /// <summary>The address an email change is moving to. Set for the EmailChange* types.</summary>
        public string? NewEmail
        {
            get; init;
        }
        public IReadOnlyList<string>? SignInProviders
        {
            get; init;
        }
        public int? EventInvitationId
        {
            get; init;
        }
        /// <summary>Enables /events/{id} deep links in CTAs. Optional; falls back to /events.</summary>
        public int? EventId
        {
            get; init;
        }
        public string? EventName
        {
            get; init;
        }
        public string? ClubName
        {
            get; init;
        }
        public string? ActorName
        {
            get; init;
        }
        public DateTime? EventStartsAtUtc
        {
            get; init;
        }
    }
}
