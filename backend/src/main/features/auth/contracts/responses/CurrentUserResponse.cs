namespace backend.main.features.auth.contracts.responses
{
    public class CurrentUserResponse
    {
        public int Id
        {
            get; set;
        }
        public string Email
        {
            get; set;
        } = null!;
        public string Username
        {
            get; set;
        } = null!;

        /// <summary>
        /// The username as its owner wrote it, e.g. <c>ThomasT</c>. Render this; resolve by
        /// <c>Username</c>.
        /// </summary>
        public string UsernameDisplay
        {
            get; set;
        } = null!;
        public string? Name
        {
            get; set;
        }
        public string? Avatar
        {
            get; set;
        }
        public string Usertype
        {
            get; set;
        } = null!;
    }
}
