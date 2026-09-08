namespace backend.main.features.auth.contracts.requests
{
    /// <summary>
    /// Confirms an email change by either proof: the token from the emailed link, or the code and
    /// challenge from the page that requested it.
    /// </summary>
    public sealed class EmailChangeConfirmationRequest
    {
        public string? Token
        {
            get; set;
        }
        public string? Code
        {
            get; set;
        }
        public string? Challenge
        {
            get; set;
        }
    }
}
