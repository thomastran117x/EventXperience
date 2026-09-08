using System.ComponentModel.DataAnnotations;

using backend.main.shared.attributes.validation;

namespace backend.main.features.auth.contracts.requests
{
    public sealed class CompleteOAuthSignupRequest
    {
        [Required]
        public required string SignupToken
        {
            get; set;
        }

        [Required]
        [ValidRole]
        public required string Usertype
        {
            get; set;
        }

        /// <summary>
        /// Required when the provider account is new, ignored when it resolves to an account that
        /// already exists. Not <c>[Required]</c> for that reason: the caller cannot know in advance
        /// which of the two it will be, and the service enforces it on the branch that creates.
        /// </summary>
        public string? Username
        {
            get; set;
        }

        public string? Transport
        {
            get; set;
        }
    }
}
