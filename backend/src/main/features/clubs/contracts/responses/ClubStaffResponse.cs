namespace backend.main.features.clubs.contracts.responses
{
    public class ClubStaffResponse
    {
        public int Id
        {
            get; set;
        }
        public int ClubId
        {
            get; set;
        }
        public int UserId
        {
            get; set;
        }
        public string Role { get; set; } = string.Empty;
        public int GrantedByUserId
        {
            get; set;
        }
        public DateTime CreatedAt
        {
            get; set;
        }
        public DateTime UpdatedAt
        {
            get; set;
        }
        public string? Name
        {
            get; set;
        }
        public string? Username
        {
            get; set;
        }

        /// <summary>
        /// The username as its owner wrote it. Render this; link and look up by <c>Username</c>.
        /// </summary>
        public string? UsernameDisplay
        {
            get; set;
        }
        public string? Avatar
        {
            get; set;
        }

        public ClubStaffResponse(
            int id,
            int clubId,
            int userId,
            string role,
            int grantedByUserId,
            DateTime createdAt,
            DateTime updatedAt,
            string? name = null,
            string? username = null,
            string? avatar = null,
            string? usernameDisplay = null)
        {
            Id = id;
            ClubId = clubId;
            UserId = userId;
            Role = role;
            GrantedByUserId = grantedByUserId;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            Name = name;
            Username = username;
            UsernameDisplay = usernameDisplay;
            Avatar = avatar;
        }
    }
}
