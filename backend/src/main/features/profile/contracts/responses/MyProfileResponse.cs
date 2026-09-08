namespace backend.main.features.profile.contracts.responses
{
    /// <summary>
    /// Full self-service profile for the authenticated user.
    /// </summary>
    public class MyProfileResponse
    {
        public int Id
        {
            get; set;
        }
        public string Email { get; set; } = null!;
        public string Username { get; set; } = null!;
        public bool CanChangeUsername
        {
            get; set;
        }
        public DateTime? UsernameChangeAvailableAtUtc
        {
            get; set;
        }
        public string? Name
        {
            get; set;
        }
        public string? Avatar
        {
            get; set;
        }
        public string Usertype { get; set; } = null!;
        public string? Phone
        {
            get; set;
        }
        public string? Address
        {
            get; set;
        }
        /// <summary>
        /// True when the account has its own password. Independent of the provider flags: linking
        /// Google or Microsoft to an existing account leaves its password in place.
        /// </summary>
        public bool HasLocalPassword
        {
            get; set;
        }
        public bool GoogleLinked
        {
            get; set;
        }
        public bool MicrosoftLinked
        {
            get; set;
        }
        public DateTime CreatedAtUtc
        {
            get; set;
        }
        public DateTime UpdatedAtUtc
        {
            get; set;
        }
    }
}
