using backend.main.features.auth;
using backend.main.features.bloom;
using backend.main.features.auth.captcha;
using backend.main.features.auth.contracts.requests;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.oauth;
using backend.main.features.auth.token;
using backend.main.features.profile;
using backend.main.features.profile.email;
using backend.main.shared.requests;
using backend.main.shared.responses;

using backend.tests.Unit.Support;

using FluentAssertions;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Moq;

using System.Security.Claims;

namespace backend.tests.Unit.Features.Auth;

public class AuthControllerTests
{
    [Fact]
    public void LocalVerify_ShouldRedirectToFrontendVerificationPage()
    {
        var controller = CreateController();

        var result = controller.LocalVerify("token with spaces");

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://frontend.example.com/auth/verify?token=token%20with%20spaces");
    }

    [Fact]
    public async Task LocalAuthenticate_ShouldWriteBrowserRefreshCookies()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.LoginAsync(
                "browser-user",
                "Password123!",
                SessionTransport.BrowserCookie,
                false,
                null))
            .ReturnsAsync(LoginAuthenticationResult.Authenticated(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(
                    "browser@example.com",
                    SessionTransport.BrowserCookie,
                    refreshToken: "browser-refresh",
                    bindingToken: "browser-binding")
            }));

        var controller = CreateController(authService: authService);

        var result = await controller.LocalAuthenticate(new LoginRequest
        {
            Username = "browser-user",
            Password = "Password123!",
            Captcha = "captcha"
        });

        var ok = result.Should().BeOfType<ObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<LoginAuthenticationResponse>>().Subject;

        response.Data.Should().NotBeNull();
        response.Data!.Auth!.RefreshToken.Should().BeNull();
        response.Data.Auth.SessionBindingToken.Should().BeNull();
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshToken="));
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshBinding="));
    }

    [Fact]
    public async Task LocalAuthenticate_ShouldReturnApiRefreshTokensForApiTransport()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.LoginAsync(
                "api-user",
                "Password123!",
                SessionTransport.ApiToken,
                false,
                null))
            .ReturnsAsync(LoginAuthenticationResult.Authenticated(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(
                    "api@example.com",
                    SessionTransport.ApiToken,
                    refreshToken: "api-refresh",
                    bindingToken: "api-binding")
            }));

        var controller = CreateController(authService: authService);

        var result = await controller.LocalAuthenticate(new LoginRequest
        {
            Username = "api-user",
            Password = "Password123!",
            Captcha = "captcha",
            Transport = SessionTransportResolver.ApiValue
        });

        var ok = result.Should().BeOfType<ObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<LoginAuthenticationResponse>>().Subject;

        response.Data.Should().NotBeNull();
        response.Data!.Auth!.RefreshToken.Should().Be("api-refresh");
        response.Data.Auth.SessionBindingToken.Should().Be("api-binding");
        controller.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public async Task LocalAuthenticate_ShouldReturnBadRequest_WhenCaptchaIsInvalid()
    {
        var captchaService = new Mock<ICaptchaService>();
        var controller = CreateController(captchaService: captchaService);
        captchaService.Setup(service => service.VerifyCaptchaAsync("bad-captcha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await controller.LocalAuthenticate(new LoginRequest
        {
            Username = "user",
            Password = "Password123!",
            Captcha = "bad-captcha"
        });

        AssertErrorResult(result, 400, "Invalid captcha.");
    }

    [Fact]
    public async Task LocalSignup_ShouldReturnVerificationChallenge()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.SignUpAsync(
                "new@example.com", "new-user", "Password123!", "Organizer"))
            .ReturnsAsync(new VerificationOtpChallenge
            {
                Code = "123456",
                Challenge = "signup-challenge",
                ExpiresAtUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc)
            });

        var controller = CreateController(authService: authService);

        var result = await controller.LocalSignup(new SignUpRequest
        {
            Email = "new@example.com",
            Username = "new-user",
            Password = "Password123!",
            Usertype = "Organizer",
            Captcha = "captcha"
        });

        var response = ExtractApiResponse<VerificationChallengeResponse>(result, 200);
        response.Message.Should().Be("Verification email sent.");
        response.Data!.Challenge.Should().Be("signup-challenge");
    }

    [Fact]
    public async Task LocalVerifyOtp_ShouldReturnSessionTokens_ForApiTransport()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.VerifyOtpAsync("123456", "otp-challenge", SessionTransport.ApiToken))
            .ReturnsAsync(CreateUserToken(
                "verified@example.com",
                SessionTransport.ApiToken,
                refreshToken: "otp-refresh",
                bindingToken: "otp-binding"));

        var controller = CreateController(authService: authService);

        var result = await controller.LocalVerifyOtp(new OtpVerificationRequest
        {
            Code = "123456",
            Challenge = "otp-challenge",
            Transport = SessionTransportResolver.ApiValue
        });

        var response = ExtractApiResponse<AuthenticatedSessionResponse>(result, 200);
        response.Message.Should().Be("Verification successful");
        response.Data!.RefreshToken.Should().Be("otp-refresh");
        response.Data.SessionBindingToken.Should().Be("otp-binding");
    }

    [Fact]
    public async Task GoogleAuthenticate_ShouldReturnRoleSelectionPayload_WhenSignupMustBeCompleted()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.GoogleAsync("google-token", SessionTransport.BrowserCookie, "nonce", null))
            .ReturnsAsync(OAuthAuthenticationResult.RoleSelectionRequired(new PendingOAuthSignupChallenge
            {
                SignupToken = "signup-token",
                Email = "oauth@example.com",
                Name = "OAuth User",
                Provider = "google"
            }));

        var controller = CreateController(authService: authService);

        var result = await controller.GoogleAuthenticate(new GoogleRequest
        {
            Token = "google-token",
            Nonce = "nonce"
        });

        var response = ExtractApiResponse<OAuthAuthenticationResponse>(result, 200);
        response.Message.Should().Be("Role selection is required to complete signup.");
        response.Data!.RequiresRoleSelection.Should().BeTrue();
        response.Data.SignupToken.Should().Be("signup-token");
        response.Data.Email.Should().Be("oauth@example.com");
    }

    [Fact]
    public async Task GoogleCodeAuthenticate_ShouldReturnAuthenticatedSessionPayload()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.GoogleCodeAsync(
                "oauth-code",
                "verifier",
                "https://frontend.example.com/callback",
                SessionTransport.ApiToken,
                "nonce",
                null))
            .ReturnsAsync(OAuthAuthenticationResult.Authenticated(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(
                    "code@example.com",
                    SessionTransport.ApiToken,
                    refreshToken: "code-refresh",
                    bindingToken: "code-binding")
            }));

        var controller = CreateController(authService: authService);

        var result = await controller.GoogleCodeAuthenticate(new GoogleCodeRequest
        {
            Code = "oauth-code",
            CodeVerifier = "verifier",
            RedirectUri = "https://frontend.example.com/callback",
            Nonce = "nonce",
            Transport = SessionTransportResolver.ApiValue
        });

        var response = ExtractApiResponse<OAuthAuthenticationResponse>(result, 200);
        response.Message.Should().Be("Login successful");
        response.Data!.RequiresRoleSelection.Should().BeFalse();
        response.Data.Auth!.RefreshToken.Should().Be("code-refresh");
    }

    [Fact]
    public async Task MicrosoftAuthenticate_ShouldReturnAuthenticatedSessionPayload()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.MicrosoftAsync(
                "microsoft-token",
                SessionTransport.ApiToken,
                "nonce",
                null))
            .ReturnsAsync(OAuthAuthenticationResult.Authenticated(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(
                    "ms@example.com",
                    SessionTransport.ApiToken,
                    refreshToken: "ms-refresh",
                    bindingToken: "ms-binding")
            }));

        var controller = CreateController(authService: authService);

        var result = await controller.MicrosoftAuthenticate(new MicrosoftRequest
        {
            Token = "microsoft-token",
            Nonce = "nonce",
            Transport = SessionTransportResolver.ApiValue
        });

        var response = ExtractApiResponse<OAuthAuthenticationResponse>(result, 200);
        response.Message.Should().Be("Login successful");
        response.Data!.RequiresRoleSelection.Should().BeFalse();
        response.Data.Auth!.RefreshToken.Should().Be("ms-refresh");
    }

    [Fact]
    public async Task Me_ShouldReturnCurrentUserResponse()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.GetCurrentUserAsync(42))
            .ReturnsAsync(new User
            {
                Id = 42,
                Email = "me@example.com",
                Username = "",
                Name = "Current User",
                Usertype = "Participant"
            });

        var controller = CreateController(authService: authService);
        controller.ControllerContext.HttpContext.User = CreatePrincipal(42, "me@example.com", "Participant");

        var result = await controller.Me();

        var response = ExtractApiResponse<CurrentUserResponse>(result, 200);
        response.Data!.Id.Should().Be(42);
        response.Data.Email.Should().Be("me@example.com");
        response.Data.Username.Should().Be("me@example.com");
        response.Data.Usertype.Should().Be("Participant");
    }

    [Fact]
    public async Task LocalVerifyPost_ShouldReturnAuthenticatedSession()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.VerifyAsync("verification-token", SessionTransport.ApiToken))
            .ReturnsAsync(CreateUserToken(
                "verified@example.com",
                SessionTransport.ApiToken,
                refreshToken: "verify-refresh",
                bindingToken: "verify-binding"));

        var controller = CreateController(authService: authService);

        var result = await controller.LocalVerify(new VerificationTokenRequest
        {
            Token = "verification-token",
            Transport = SessionTransportResolver.ApiValue
        });

        var response = ExtractApiResponse<AuthenticatedSessionResponse>(result, 200);
        response.Message.Should().Be("Verification successful");
        response.Data!.RefreshToken.Should().Be("verify-refresh");
    }

    [Fact]
    public async Task Refresh_ShouldReturnUnauthorized_AndClearCookies_WhenRefreshTokenIsMissing()
    {
        var controller = CreateController();

        var result = await controller.Refresh(null);

        AssertErrorResult(result, 401, "Missing refresh token");
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshToken="));
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshBinding="));
    }

    [Fact]
    public async Task ApiRefresh_ShouldReturnSessionPayload_FromRequestBodyTokens()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.HandleTokensAsync("api-refresh", "api-binding", SessionTransport.ApiToken))
            .ReturnsAsync(CreateUserToken(
                "api-refresh@example.com",
                SessionTransport.ApiToken,
                refreshToken: "new-refresh",
                bindingToken: "new-binding"));

        var controller = CreateController(authService: authService);

        var result = await controller.ApiRefresh(new RefreshTokenRequest
        {
            RefreshToken = "api-refresh",
            SessionBindingToken = "api-binding"
        });

        var response = ExtractApiResponse<AuthenticatedSessionResponse>(result, 200);
        response.Message.Should().Be("Session refreshed successfully.");
        response.Data!.RefreshToken.Should().Be("new-refresh");
        response.Data.SessionBindingToken.Should().Be("new-binding");
    }

    [Fact]
    public void Csrf_ShouldReturnAntiforgeryToken()
    {
        var antiforgery = new Mock<IAntiforgery>();
        var controller = CreateController(antiforgery: antiforgery);
        antiforgery.Setup(service => service.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(new AntiforgeryTokenSet("csrf-token", "cookie-token", "form-field", "header-name"));

        var result = controller.Csrf();

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ((ApiResponse<object>)ok.Value!).Data!;
        payload.Should().NotBeNull();
        payload!.GetType().GetProperty("token")!.GetValue(payload).Should().Be("csrf-token");
    }

    [Fact]
    public async Task Logout_ShouldReturnAlreadyLoggedOut_WhenBrowserTokenIsMissing()
    {
        var controller = CreateController();

        var result = await controller.Logout(null);

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("The user is already logged out.");
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshToken="));
    }

    [Fact]
    public async Task ApiLogout_ShouldReturnUnauthorized_WhenBindingTokenIsMissing()
    {
        var controller = CreateController();

        var result = await controller.ApiLogout(new RefreshTokenRequest
        {
            RefreshToken = "api-refresh"
        });

        AssertErrorResult(result, 401, "Missing session binding token");
    }

    [Fact]
    public async Task Logout_ShouldClearCookiesAndCallService_WhenBrowserTokensExist()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.HandleLogoutAsync(
                "browser-refresh",
                "browser-binding",
                SessionTransport.BrowserCookie))
            .Returns(Task.CompletedTask);
        var controller = CreateController(authService: authService);
        controller.Request.Headers.Cookie = "refreshToken=browser-refresh; refreshBinding=browser-binding";

        var result = await controller.Logout(null);

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("The user's logout is successful");
        controller.Response.Headers.SetCookie.Should().Contain(value => value.Contains("refreshToken="));
        authService.VerifyAll();
    }

    [Fact]
    public async Task ApiLogout_ShouldCallService_WhenRequestBodyContainsTokens()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.HandleLogoutAsync(
                "api-refresh",
                "api-binding",
                SessionTransport.ApiToken))
            .Returns(Task.CompletedTask);
        var controller = CreateController(authService: authService);

        var result = await controller.ApiLogout(new RefreshTokenRequest
        {
            RefreshToken = "api-refresh",
            SessionBindingToken = "api-binding"
        });

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("The user's logout is successful");
        authService.VerifyAll();
    }

    [Fact]
    public void VerifyDeviceGet_ShouldRedirectToFrontendDeviceVerificationPage()
    {
        var controller = CreateController();

        var result = controller.VerifyDevice("device token");

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://frontend.example.com/auth/device/verify?token=device%20token");
    }

    [Fact]
    public async Task VerifyDevicePost_ShouldReturnAuthenticatedSession()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.VerifyDeviceLoginAsync("device-token", SessionTransport.ApiToken))
            .ReturnsAsync(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(
                    "device@example.com",
                    SessionTransport.ApiToken,
                    refreshToken: "device-refresh",
                    bindingToken: "device-binding")
            });
        var controller = CreateController(authService: authService);

        var result = await controller.VerifyDevice(new VerificationTokenRequest
        {
            Token = "device-token",
            Transport = SessionTransportResolver.ApiValue
        });

        var response = ExtractApiResponse<AuthenticatedSessionResponse>(result, 200);
        response.Message.Should().Be("Device verified. Login successful.");
        response.Data!.RefreshToken.Should().Be("device-refresh");
    }

    [Fact]
    public async Task ForgotPassword_ShouldReturnBadRequest_WhenCaptchaIsInvalid()
    {
        var captchaService = new Mock<ICaptchaService>();
        var controller = CreateController(captchaService: captchaService);
        captchaService.Setup(service => service.VerifyCaptchaAsync("bad-captcha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await controller.ForgotPassword(new ForgotPasswordRequest
        {
            Username = "forgot-user",
            Captcha = "bad-captcha"
        });

        AssertErrorResult(result, 400, "Invalid captcha.");
    }

    [Fact]
    public async Task ForgotPassword_ShouldReturnVerificationChallenge_WhenCaptchaIsValid()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.RecoverPasswordAsync("forgot-user"))
            .ReturnsAsync(new VerificationOtpChallenge
            {
                Code = "123456",
                Challenge = "forgot-challenge",
                ExpiresAtUtc = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)
            });
        var controller = CreateController(authService: authService);

        var result = await controller.ForgotPassword(new ForgotPasswordRequest
        {
            Username = "forgot-user",
            Captcha = "captcha"
        });

        var response = ExtractApiResponse<VerificationChallengeResponse>(result, 200);
        response.Message.Should().Be("If the account exists, recovery instructions have been sent.");
        response.Data!.Challenge.Should().Be("forgot-challenge");
    }

    [Fact]
    public async Task RecoverUsername_ShouldReturnGenericMessage_WhenCaptchaIsValid()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.RecoverUsernameAsync("member@example.com"))
            .Returns(Task.CompletedTask);
        var controller = CreateController(authService: authService);

        var result = await controller.RecoverUsername(new UsernameRecoveryRequest
        {
            Email = "member@example.com",
            Captcha = "captcha"
        });

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("If the account exists, recovery instructions have been sent.");
        authService.Verify(service => service.RecoverUsernameAsync("member@example.com"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_ShouldUseTokenFlow_WhenQueryTokenIsPresent()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.ResetPasswordAsync("reset-token", "Password123!"))
            .Returns(Task.CompletedTask);
        var controller = CreateController(authService: authService);

        var result = await controller.ChangePassword(
            new ChangePasswordRequest { Password = "Password123!" },
            "reset-token");

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("Password reset successful. Please sign in.");
        authService.Verify(service => service.ResetPasswordAsync("reset-token", "Password123!"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_ShouldUseOtpFlow_WhenChallengeAndCodeArePresent()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.ResetPasswordWithOtpAsync("123456", "challenge", "Password123!"))
            .Returns(Task.CompletedTask);
        var controller = CreateController(authService: authService);

        var result = await controller.ChangePassword(
            new ChangePasswordRequest
            {
                Password = "Password123!",
                Code = "123456",
                Challenge = "challenge"
            },
            null);

        var response = ExtractMessageResponse(result, 200);
        response.Message.Should().Be("Password reset successful. Please sign in.");
        authService.Verify(service => service.ResetPasswordWithOtpAsync("123456", "challenge", "Password123!"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_ShouldReturnBadRequest_WhenResetProofIsMissing()
    {
        var controller = CreateController();

        var result = await controller.ChangePassword(
            new ChangePasswordRequest { Password = "Password123!" },
            null);

        AssertErrorResult(result, 400, "Missing password reset token or OTP challenge.");
    }

    [Fact]
    public async Task CheckEmailAvailability_ShouldReportAnUnknownAddressAsAvailable()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        emailAvailability.Setup(service => service.IsRegisteredAsync(
                "ada@example.com",
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = CreateController(emailAvailability: emailAvailability);

        var result = await controller.CheckEmailAvailability("ada@example.com", CancellationToken.None);

        var body = AssertOkResult<EmailAvailabilityResponse>(result, "Email is available.");
        body.Email.Should().Be("ada@example.com");
        body.Available.Should().BeTrue();
    }

    [Fact]
    public async Task CheckEmailAvailability_ShouldReportARegisteredAddressAsUnavailable()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        emailAvailability.Setup(service => service.IsRegisteredAsync(
                "ada@example.com",
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = CreateController(emailAvailability: emailAvailability);

        var result = await controller.CheckEmailAvailability("ada@example.com", CancellationToken.None);

        var body = AssertOkResult<EmailAvailabilityResponse>(result, "Email is already registered.");
        body.Available.Should().BeFalse();
    }

    /// <summary>
    /// The probe must evaluate the same string the filter was seeded with, and echo it back so the
    /// client can see what was actually checked.
    /// </summary>
    [Fact]
    public async Task CheckEmailAvailability_ShouldNormaliseBeforeLookingUp()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var controller = CreateController(emailAvailability: emailAvailability);

        var result = await controller.CheckEmailAvailability("  Ada@Example.COM  ", CancellationToken.None);

        AssertOkResult<EmailAvailabilityResponse>(result, "Email is available.")
            .Email.Should().Be("ada@example.com");
        emailAvailability.Verify(service => service.IsRegisteredAsync(
                "ada@example.com",
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Advisory is what makes a type-ahead probe cheap; an authoritative probe here would query
    /// the database on every keystroke pause and defeat the filter entirely.
    /// </summary>
    [Fact]
    public async Task CheckEmailAvailability_ShouldAskAdvisorily()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var controller = CreateController(emailAvailability: emailAvailability);

        await controller.CheckEmailAvailability("ada@example.com", CancellationToken.None);

        emailAvailability.Verify(service => service.IsRegisteredAsync(
                It.IsAny<string>(),
                AvailabilityLookupMode.Authoritative,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("", "Email is required.")]
    [InlineData("   ", "Email is required.")]
    [InlineData("not-an-address", "Email must be a valid email address.")]
    public async Task CheckEmailAvailability_ShouldRejectMalformedInput(string email, string expected)
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var controller = CreateController(emailAvailability: emailAvailability);

        var result = await controller.CheckEmailAvailability(email, CancellationToken.None);

        AssertErrorResult(result, 400, expected);
        // A malformed value never reaches the filter or the database.
        emailAvailability.Verify(service => service.IsRegisteredAsync(
                It.IsAny<string>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AuthController CreateController(
        Mock<IAuthService>? authService = null,
        Mock<ICaptchaService>? captchaService = null,
        Mock<IAntiforgery>? antiforgery = null,
        Mock<IEmailAvailabilityService>? emailAvailability = null)
    {
        authService ??= new Mock<IAuthService>();
        captchaService ??= new Mock<ICaptchaService>();
        antiforgery ??= new Mock<IAntiforgery>();
        captchaService.Setup(service => service.VerifyCaptchaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        antiforgery.Setup(service => service.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(new AntiforgeryTokenSet("csrf-default", "cookie-default", "form", "header"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Frontend:BaseUrl"] = "https://frontend.example.com"
            })
            .Build();

        var controller = new AuthController(
            authService.Object,
            new UsernameAvailabilityService(new Mock<IAuthUserRepository>().Object, new DisabledBloomFilterRegistry()),
            emailAvailability?.Object
                ?? new EmailAvailabilityService(
                    new Mock<IAuthUserRepository>().Object,
                    new DisabledBloomFilterRegistry()),
            antiforgery.Object,
            captchaService.Object,
            new SeedAccountBypassPolicy(configuration),
            TestRequestInfoFactory.Browser(),
            configuration,
            new Mock<ITokenService>().Object,
            new Mock<IEmailChangeService>().Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    private static UserToken CreateUserToken(
        string email,
        SessionTransport transport,
        string refreshToken,
        string bindingToken)
    {
        return new UserToken(
            new Token(
                "access-token",
                DateTime.UtcNow.AddMinutes(15),
                refreshToken,
                bindingToken,
                TimeSpan.FromDays(1),
                transport),
            new TestUserBuilder().WithEmail(email).Build());
    }

    private static ClaimsPrincipal CreatePrincipal(int id, string email, string role)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth"));
    }

    private static ApiResponse<T> ExtractApiResponse<T>(IActionResult result, int expectedStatusCode)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatusCode);
        return objectResult.Value.Should().BeOfType<ApiResponse<T>>().Subject;
    }

    private static MessageResponse ExtractMessageResponse(IActionResult result, int expectedStatusCode)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatusCode);
        return objectResult.Value.Should().BeOfType<MessageResponse>().Subject;
    }

    private static T AssertOkResult<T>(IActionResult result, string expectedMessage)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(200);
        var response = objectResult.Value.Should().BeOfType<ApiResponse<T>>().Subject;
        response.Message.Should().Be(expectedMessage);
        return response.Data!;
    }

    private static void AssertErrorResult(IActionResult result, int expectedStatusCode, string expectedMessage)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatusCode);
        var response = objectResult.Value.Should().BeOfType<ApiResponse<object?>>().Subject;
        response.Message.Should().Be(expectedMessage);
        response.Success.Should().BeFalse();
    }
}
