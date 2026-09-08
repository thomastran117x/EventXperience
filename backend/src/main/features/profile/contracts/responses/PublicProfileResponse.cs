namespace backend.main.features.profile.contracts.responses
{
    /// <summary>
    /// Publicly viewable profile fields for any user, keyed by username.
    /// Deliberately excludes the internal id, email, phone, and address.
    /// </summary>
    public class PublicProfileResponse
    {
        public string Username { get; set; } = null!;

        /// <summary>
        /// The username as its owner wrote it, e.g. <c>ThomasT</c>. Render this; resolve by
        /// <c>Username</c>.
        /// </summary>
        public string UsernameDisplay { get; set; } = null!;
        public string? Name
        {
            get; set;
        }
        public string? Avatar
        {
            get; set;
        }
        public string Usertype { get; set; } = null!;
        public DateTime CreatedAtUtc
        {
            get; set;
        }
    }
}
