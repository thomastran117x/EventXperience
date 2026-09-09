namespace backend.main.features.profile.contracts
{
    public sealed class UserListRecord
    {
        public int Id
        {
            get; init;
        }
        public string Email { get; init; } = null!;
        public string Username { get; init; } = null!;

        /// <summary>
        /// The username as its owner wrote it. Presentation only — never look an account up by it.
        /// </summary>
        public string UsernameDisplay { get; init; } = null!;
        public string? Name
        {
            get; init;
        }
        public string? Avatar
        {
            get; init;
        }
        public string Usertype { get; init; } = null!;
        public bool? IsDisabled
        {
            get; init;
        }
        public DateTime? DisabledAtUtc
        {
            get; init;
        }
        public string? DisabledReason
        {
            get; init;
        }
        public DateTime? CreatedAt
        {
            get; init;
        }
        public DateTime? UpdatedAt
        {
            get; init;
        }
    }
}
