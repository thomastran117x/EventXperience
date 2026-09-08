namespace backend.main.features.auth.contracts
{
    public sealed class UserAuthRecord
    {
        public int Id
        {
            get; init;
        }
        public string Email { get; init; } = null!;
        public string? Password
        {
            get; init;
        }
        public string Usertype { get; init; } = null!;
        public string? Name
        {
            get; init;
        }
        public bool IsDisabled
        {
            get; init;
        }
        public int AuthVersion
        {
            get; init;
        }
    }
}
