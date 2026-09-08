using System.ComponentModel.DataAnnotations;

using backend.main.features.auth.contracts.requests;

using FluentAssertions;

namespace backend.tests.Unit.Features.Auth;

public class AuthRequestValidationTests
{
    [Fact]
    public void SignUpRequest_ShouldRequireValidEmailStrongPasswordCaptchaAndKnownRole()
    {
        var request = new SignUpRequest
        {
            Email = "not-an-email",
            Username = "",
            Password = "weak",
            Captcha = "",
            Usertype = "guest"
        };

        var results = Validate(request);

        results.Select(item => item.ErrorMessage).Should().Contain([
            "The Email field is not a valid e-mail address.",
            "The Username field is required.",
            "Password must be at least 8 characters long.",
            "The Captcha field is required.",
            "Role must be one of: 'Participant', 'Organizer', 'Volunteer'."
        ]);
    }

    [Fact]
    public void SignUpRequest_ShouldValidateSuccessfully_ForWellFormedPayload()
    {
        var request = new SignUpRequest
        {
            Email = "member@test.local",
            Username = "member",
            Password = "StrongPass1!",
            Captcha = "captcha-token",
            Usertype = " organizer "
        };

        Validate(request).Should().BeEmpty();
    }

    [Fact]
    public void LoginRequest_ShouldRequireUsernamePasswordAndCaptcha()
    {
        var request = new LoginRequest
        {
            Username = "",
            Password = "",
            Captcha = ""
        };

        var results = Validate(request);

        results.Select(item => item.ErrorMessage).Should().Contain([
            "The Username field is required.",
            "The Captcha field is required."
        ]);
    }

    [Fact]
    public void ChangePasswordRequest_ShouldEnforceStrongPasswordAndCodeLength()
    {
        var request = new ChangePasswordRequest
        {
            Password = "NoNumber!",
            Code = "123"
        };

        var results = Validate(request);

        results.Select(item => item.ErrorMessage).Should().Contain([
            "Password must contain at least one number.",
            "Code must be 6 digits."
        ]);
    }

    [Fact]
    public void RecoveryRequests_ShouldRequireCorrectIdentifierTypes()
    {
        var passwordResults = Validate(new PasswordRecoveryRequest
        {
            Username = "",
            Captcha = ""
        });
        var usernameResults = Validate(new UsernameRecoveryRequest
        {
            Email = "not-an-email",
            Captcha = "captcha"
        });

        passwordResults.Select(item => item.ErrorMessage).Should().Contain([
            "The Username field is required.",
            "The Captcha field is required."
        ]);
        usernameResults.Select(item => item.ErrorMessage).Should().Contain(
            "The Email field is not a valid e-mail address."
        );
    }

    /// <summary>
    /// Username is enforced in the service, on the branch that creates an account, not by the
    /// annotations: the caller cannot know whether the provider account is new, and the link
    /// branch legitimately sends none.
    /// </summary>
    [Fact]
    public void CompleteOAuthSignupRequest_ShouldNotRequireAUsername()
    {
        var request = new CompleteOAuthSignupRequest
        {
            SignupToken = "signup-token",
            Usertype = "organizer",
            Username = null
        };

        Validate(request).Should().BeEmpty();
    }

    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
