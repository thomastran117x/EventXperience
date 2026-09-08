namespace backend.main.shared.responses
{
    public class AuthorInfo
    {
        public int Id
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
    }
}
