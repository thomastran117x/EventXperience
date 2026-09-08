using backend.main.application.security;
using backend.main.features.auth;
using backend.main.features.profile.suggestions;
using backend.main.features.bloom;
using backend.main.features.auth.captcha;
using backend.main.features.auth.contracts.requests;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.oauth;
using backend.main.features.auth.stepup;
using backend.main.features.auth.token;
using backend.main.features.profile.email;
using backend.main.features.cache;
using backend.main.shared.requests;
using backend.main.shared.responses;

using backend.tests.Unit.Support;

using FluentAssertions;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

using Moq;

namespace backend.tests.Unit.Features.Auth;

public class AuthTotpStepUpControllerTests
{
    [Fact]
    public async Task VerifyTotpStepUp_ShouldReturnAuthenticatedSession()
    {
        var authService = new Mock<IAuthService>();
        authService.Setup(service => service.VerifyTotpLoginStepUpAsync("challenge-1", "123456"))
            .ReturnsAsync(new AuthenticatedSessionResult
            {
                UserToken = CreateUserToken(),
                ReturnPath = "/bookings/42"
            });

        var controller = CreateController(authService: authService);

        var result = await controller.VerifyTotpStepUp(new VerifyLoginStepUpRequest
        {
            Challenge = "challenge-1",
            Code = "123456"
        });

        var response = ExtractApiResponse<AuthenticatedSessionResponse>(result, 200);
        response.Message.Should().Be("Sign-in verification successful.");
        response.Data!.AccessToken.Should().Be("access-token");
        response.Data.ReturnPath.Should().Be("/bookings/42");
    }

    private static AuthController CreateController(
        Mock<IAuthService>? authService = null,
        Mock<ICaptchaService>? captchaService = null,
        Mock<IAntiforgery>? antiforgery = null)
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

        return new AuthController(
            authService.Object,
            new UsernameAvailabilityService(new Mock<IAuthUserRepository>().Object, new DisabledBloomFilterRegistry()),
            new Mock<IUsernameSuggestionService>().Object,
            new EmailAvailabilityService(new Mock<IAuthUserRepository>().Object, new DisabledBloomFilterRegistry()),
            antiforgery.Object,
            captchaService.Object,
            new SeedAccountBypassPolicy(configuration),
            TestRequestInfoFactory.Browser(),
            configuration,
            new Mock<ITokenService>().Object,
            new Mock<IEmailChangeService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static UserToken CreateUserToken()
    {
        return new UserToken(
            new Token(
                "access-token",
                DateTime.UtcNow.AddMinutes(15),
                "refresh-token",
                "binding-token",
                TimeSpan.FromDays(1),
                SessionTransport.ApiToken),
            new TestUserBuilder().WithEmail("member@example.com").Build());
    }

    private static ApiResponse<T> ExtractApiResponse<T>(IActionResult result, int expectedStatusCode)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatusCode);
        return objectResult.Value.Should().BeOfType<ApiResponse<T>>().Subject;
    }
}

public class AuthTotpStepUpServiceTests
{
    [Fact]
    public async Task VerifyTotpLoginStepUpAsync_ShouldDelegateToChallengeService()
    {
        var challengeService = new Mock<ILoginStepUpChallengeService>();
        var expected = new AuthenticatedSessionResult
        {
            UserToken = new UserToken(
                new Token(
                    "access-token",
                    DateTime.UtcNow.AddMinutes(15),
                    "refresh-token",
                    "binding-token",
                    TimeSpan.FromDays(1),
                    SessionTransport.ApiToken),
                new TestUserBuilder().WithEmail("member@example.com").Build()),
            ReturnPath = "/events/5"
        };

        challengeService.Setup(service => service.VerifyTotpAsync("challenge-1", "123456"))
            .ReturnsAsync(expected);

        var service = CreateService(loginStepUpChallengeService: challengeService);

        var result = await service.VerifyTotpLoginStepUpAsync("challenge-1", "123456");

        result.Should().BeSameAs(expected);
        challengeService.Verify(service => service.VerifyTotpAsync("challenge-1", "123456"), Times.Once);
    }

    private static AuthService CreateService(
        Mock<IAuthUserRepository>? userRepository = null,
        Mock<IOAuthService>? oauthService = null,
        Mock<ITokenService>? tokenService = null,
        Mock<ICacheService>? cacheService = null,
        Mock<IAuthNotificationService>? authNotificationService = null,
        Mock<IDeviceService>? deviceService = null,
        Mock<ITotpMfaEnrollmentService>? totpMfaEnrollmentService = null,
        Mock<IDeviceTrustService>? deviceTrustService = null,
        Mock<ILoginStepUpChallengeService>? loginStepUpChallengeService = null,
        Mock<IAuthSessionService>? authSessionService = null)
    {
        userRepository ??= new Mock<IAuthUserRepository>();
        oauthService ??= new Mock<IOAuthService>();
        tokenService ??= new Mock<ITokenService>();
        cacheService ??= new Mock<ICacheService>();
        authNotificationService ??= new Mock<IAuthNotificationService>();
        deviceService ??= new Mock<IDeviceService>();
        totpMfaEnrollmentService ??= new Mock<ITotpMfaEnrollmentService>();
        deviceTrustService ??= new Mock<IDeviceTrustService>();
        loginStepUpChallengeService ??= new Mock<ILoginStepUpChallengeService>();
        authSessionService ??= new Mock<IAuthSessionService>();

        return new AuthService(
            userRepository.Object,
            oauthService.Object,
            tokenService.Object,
            cacheService.Object,
            authNotificationService.Object,
            deviceService.Object,
            totpMfaEnrollmentService.Object,
            deviceTrustService.Object,
            loginStepUpChallengeService.Object,
            authSessionService.Object,
            new UsernameAvailabilityService(userRepository.Object, new DisabledBloomFilterRegistry()),
            new EmailAvailabilityService(userRepository.Object, new DisabledBloomFilterRegistry()),
            new SeedAccountBypassPolicy(new ConfigurationBuilder().Build()),
            TestRequestInfoFactory.Browser());
    }
}
