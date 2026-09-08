using backend.main.features.auth;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.stepup;
using backend.main.features.auth.token;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;

using backend.tests.Unit.Support;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Auth;

public class LoginStepUpChallengeServiceTests
{
    [Fact]
    public async Task CreateChallengeAsync_ShouldIncludeSmsAndEmail_WhenSmsEnrolled()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 5, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(5).WithEmail("test@example.com").Build();
        var service = CreateService(mfaRepo: mfaRepo);

        var response = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/dashboard");

        response.Challenge.Should().NotBeNullOrWhiteSpace();
        response.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        response.AvailableMethods.Should().Contain("sms").And.Contain("email");
        response.MaskedPhone.Should().NotBeNullOrWhiteSpace();
        response.MaskedEmail.Should().Be("t***@example.com");
    }

    [Fact]
    public async Task CreateChallengeAsync_ShouldIncludeEmailOnly_WhenNoSmsEnrollment()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();
        var service = CreateService(mfaRepo: mfaRepo);

        var response = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);

        response.AvailableMethods.Should().ContainSingle().Which.Should().Be("email");
        response.MaskedPhone.Should().BeNull();
    }

    [Fact]
    public async Task CreateChallengeAsync_ShouldNormalizeInvalidReturnUrl_ToDashboard()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(r => r.GetUserAsync(5)).ReturnsAsync(user);
        var deviceTrustService = new Mock<IDeviceTrustService>();
        deviceTrustService.Setup(d => d.TrustAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var authSessionService = new Mock<IAuthSessionService>();
        authSessionService.Setup(s => s.IssueAsync(It.IsAny<User>(), It.IsAny<SessionTransport>(), It.IsAny<string?>(), It.IsAny<bool?>()))
            .ReturnsAsync(CreateUserToken(user));

        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        string? capturedToken = null;
        notifications.Setup(n => n.SendDeviceVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string, string?>((_, token, _) => capturedToken = token)
            .Returns(Task.CompletedTask);

        var service = CreateService(cache, notifications, mfaRepo, deviceTrustService: deviceTrustService, userRepository: userRepository, authSessionService: authSessionService);
        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "http://evil.com/redirect");
        await service.StartAsync(challenge.Challenge, "email");

        var result = await service.TryVerifyEmailAsync(capturedToken!);

        result!.ReturnPath.Should().Be("/dashboard");
    }

    /// <summary>
    /// A challenge that outlives a global sign-out must not still mint a session.
    /// </summary>
    /// <remarks>
    /// Revoking sessions cannot reach a challenge that is mid-flight, because it does not hold a
    /// session yet — it holds a claim on being about to get one. So a password or email change
    /// that signed every device out would otherwise be followed by whoever had already passed the
    /// password check completing their challenge into a brand new session. The stamped auth
    /// version is what closes that window.
    /// </remarks>
    [Fact]
    public async Task TryVerifyEmailAsync_ShouldRefuse_WhenTheAccountWasSignedOutMidChallenge()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();

        // The account as it stands when the challenge is finally completed: something
        // security-sensitive has happened since, so the auth version has moved on.
        var reloaded = new TestUserBuilder().WithId(5).WithEmail("changed@example.com").Build();
        reloaded.AuthVersion = user.AuthVersion + 1;

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(r => r.GetUserAsync(5)).ReturnsAsync(reloaded);

        var deviceTrustService = new Mock<IDeviceTrustService>();
        deviceTrustService
            .Setup(d => d.TrustAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var authSessionService = new Mock<IAuthSessionService>();
        authSessionService
            .Setup(s => s.IssueAsync(
                It.IsAny<User>(), It.IsAny<SessionTransport>(),
                It.IsAny<string?>(), It.IsAny<bool?>()))
            .ReturnsAsync(CreateUserToken(user));

        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        string? capturedToken = null;
        notifications
            .Setup(n => n.SendDeviceVerificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string, string?>((_, token, _) => capturedToken = token)
            .Returns(Task.CompletedTask);

        var service = CreateService(
            cache,
            notifications,
            mfaRepo,
            deviceTrustService: deviceTrustService,
            userRepository: userRepository,
            authSessionService: authSessionService);

        var challenge = await service.CreateChallengeAsync(
            user, SessionTransport.ApiToken, false, "/dashboard");
        await service.StartAsync(challenge.Challenge, "email");

        var act = () => service.TryVerifyEmailAsync(capturedToken!);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("*no longer valid*");
        authSessionService.Verify(
            s => s.IssueAsync(
                It.IsAny<User>(), It.IsAny<SessionTransport>(),
                It.IsAny<string?>(), It.IsAny<bool?>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_ShouldSendSmsCode_AndReturnNewChallenge()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        notifications.Setup(n => n.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 5, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(5).WithEmail("test@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/return");
        var startResponse = await service.StartAsync(challenge.Challenge, "sms");

        startResponse.SelectedMethod.Should().Be("sms");
        startResponse.Challenge.Should().NotBe(challenge.Challenge);
        startResponse.AvailableMethods.Should().Contain("sms");
        startResponse.MaskedPhone.Should().NotBeNullOrWhiteSpace();
        notifications.Verify(n => n.SendSmsMfaAsync("+14165550123", It.IsAny<string>(), startResponse.Challenge, It.IsAny<DateTime>(), "sign-in verification"), Times.Once);
    }

    [Fact]
    public async Task StartAsync_ShouldSendEmailToken_AndReturnNewChallenge()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        string? capturedToken = null;
        notifications.Setup(n => n.SendDeviceVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string, string?>((_, token, _) => capturedToken = token)
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, true, null);
        var startResponse = await service.StartAsync(challenge.Challenge, "email");

        startResponse.SelectedMethod.Should().Be("email");
        startResponse.Challenge.Should().NotBe(challenge.Challenge);
        startResponse.MaskedEmail.Should().NotBeNullOrWhiteSpace();
        capturedToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAsync_ShouldThrowUnauthorized_WhenChallengeNotFound()
    {
        var service = CreateService();

        var act = () => service.StartAsync("invalid-challenge-token", "email");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification challenge.");
    }

    [Fact]
    public async Task StartAsync_ShouldThrowBadRequest_WhenMethodIsUnsupported()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();
        var service = CreateService(mfaRepo: mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);
        var act = () => service.StartAsync(challenge.Challenge, "push-notification");

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("Unsupported sign-in verification method.");
    }

    [Fact]
    public async Task StartAsync_ShouldThrowBadRequest_WhenSmsRequestedButNotAvailable()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(5).WithEmail("user@example.com").Build();
        var service = CreateService(mfaRepo: mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);
        var act = () => service.StartAsync(challenge.Challenge, "sms");

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("The requested sign-in verification method is unavailable.");
    }

    [Fact]
    public async Task VerifySmsAsync_ShouldReturnAuthResult_WhenCodeIsCorrect()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        string? capturedSmsCode = null;
        notifications.Setup(n => n.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Callback<string, string, string, DateTime, string>((_, code, _, _, _) => capturedSmsCode = code)
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(7))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 7, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(7).WithEmail("user@example.com").Build();
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(r => r.GetUserAsync(7)).ReturnsAsync(user);

        var deviceTrustService = new Mock<IDeviceTrustService>();
        deviceTrustService.Setup(d => d.TrustAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var authSessionService = new Mock<IAuthSessionService>();
        var expectedToken = CreateUserToken(user);
        authSessionService.Setup(s => s.IssueAsync(It.IsAny<User>(), It.IsAny<SessionTransport>(), It.IsAny<string?>(), It.IsAny<bool?>()))
            .ReturnsAsync(expectedToken);

        var service = CreateService(cache, notifications, mfaRepo, deviceTrustService: deviceTrustService, userRepository: userRepository, authSessionService: authSessionService);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/bookings");
        var startResponse = await service.StartAsync(challenge.Challenge, "sms");

        var result = await service.VerifySmsAsync(startResponse.Challenge, capturedSmsCode!);

        result.UserToken.Should().Be(expectedToken);
        result.ReturnPath.Should().Be("/bookings");
        deviceTrustService.Verify(d => d.TrustAsync(7, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task VerifySmsAsync_ShouldThrowUnauthorized_WhenChallengeNotFound()
    {
        var service = CreateService();

        var act = () => service.VerifySmsAsync("invalid-challenge-token", "123456");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification challenge.");
    }

    [Fact]
    public async Task VerifySmsAsync_ShouldThrowUnauthorized_WhenCodeIsIncorrect()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        notifications.Setup(n => n.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(7))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 7, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(7).WithEmail("user@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);
        var startResponse = await service.StartAsync(challenge.Challenge, "sms");

        var act = () => service.VerifySmsAsync(startResponse.Challenge, "000000");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification code.");
    }

    [Fact]
    public async Task VerifySmsAsync_ShouldThrowUnauthorized_WhenNoSmsCodePendingForChallenge()
    {
        var cache = new InMemoryCacheService();
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(7))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 7, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(7).WithEmail("user@example.com").Build();
        var service = CreateService(cache: cache, mfaRepo: mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);

        var act = () => service.VerifySmsAsync(challenge.Challenge, "123456");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification code.");
    }

    [Fact]
    public async Task TryVerifyEmailAsync_ShouldReturnNull_WhenTokenNotFound()
    {
        var service = CreateService();

        var result = await service.TryVerifyEmailAsync("nonexistent-email-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryVerifyEmailAsync_ShouldReturnAuthResult_WhenTokenIsValid()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        string? capturedEmailToken = null;
        notifications.Setup(n => n.SendDeviceVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string, string?>((_, token, _) => capturedEmailToken = token)
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(8)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(8).WithEmail("email@example.com").Build();
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(r => r.GetUserAsync(8)).ReturnsAsync(user);

        var deviceTrustService = new Mock<IDeviceTrustService>();
        deviceTrustService.Setup(d => d.TrustAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var authSessionService = new Mock<IAuthSessionService>();
        var expectedToken = CreateUserToken(user);
        authSessionService.Setup(s => s.IssueAsync(It.IsAny<User>(), It.IsAny<SessionTransport>(), It.IsAny<string?>(), It.IsAny<bool?>()))
            .ReturnsAsync(expectedToken);

        var service = CreateService(cache, notifications, mfaRepo, deviceTrustService: deviceTrustService, userRepository: userRepository, authSessionService: authSessionService);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.BrowserCookie, true, "/events");
        await service.StartAsync(challenge.Challenge, "email");

        var result = await service.TryVerifyEmailAsync(capturedEmailToken!);

        result.Should().NotBeNull();
        result!.UserToken.Should().Be(expectedToken);
        result.ReturnPath.Should().Be("/events");
        deviceTrustService.Verify(d => d.TrustAsync(8, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task VerifySmsAsync_ShouldExpireChallenge_AfterMaxFailedAttempts()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        notifications.Setup(n => n.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(9))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 9, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(9).WithEmail("user@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);
        var startResponse = await service.StartAsync(challenge.Challenge, "sms");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongCode = () => service.VerifySmsAsync(startResponse.Challenge, "000000");
            await wrongCode.Should().ThrowAsync<UnauthorizedException>();
        }

        var afterMaxAttempts = () => service.VerifySmsAsync(startResponse.Challenge, "000000");
        await afterMaxAttempts.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification challenge.");
    }

    [Fact]
    public async Task StartAsync_ShouldThrowTooManyRequests_WhenCooldownIsActive()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        notifications.Setup(n => n.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(9))
            .ReturnsAsync(new SmsMfaEnrollment { UserId = 9, PhoneNumber = "+14165550123", IsSmsMfaEnabled = true });

        var user = new TestUserBuilder().WithId(9).WithEmail("user@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);
        var firstStart = await service.StartAsync(challenge.Challenge, "sms");

        var act = () => service.StartAsync(firstStart.Challenge, "sms");

        await act.Should().ThrowAsync<TooManyRequestException>();
    }

    [Fact]
    public async Task CreateChallengeAsync_ShouldMaskEmailWithoutAtSign_AsStars()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(r => r.GetByUserIdAsync(10)).ReturnsAsync((SmsMfaEnrollment?)null);

        var user = new TestUserBuilder().WithId(10).WithEmail("nodomain").Build();
        var service = CreateService(mfaRepo: mfaRepo);

        var response = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);

        response.MaskedEmail.Should().Be("***");
    }

    private static LoginStepUpChallengeService CreateService(
        InMemoryCacheService? cache = null,
        Mock<IAuthNotificationService>? notifications = null,
        Mock<IMfaEnrollmentRepository>? mfaRepo = null,
        Mock<ITotpMfaEnrollmentService>? totpService = null,
        Mock<IDeviceTrustService>? deviceTrustService = null,
        Mock<IAuthUserRepository>? userRepository = null,
        Mock<IAuthSessionService>? authSessionService = null)
    {
        cache ??= new InMemoryCacheService();
        notifications ??= new Mock<IAuthNotificationService>();
        mfaRepo ??= new Mock<IMfaEnrollmentRepository>();
        totpService ??= new Mock<ITotpMfaEnrollmentService>();
        deviceTrustService ??= new Mock<IDeviceTrustService>();
        userRepository ??= new Mock<IAuthUserRepository>();
        authSessionService ??= new Mock<IAuthSessionService>();

        return new LoginStepUpChallengeService(
            cache,
            notifications.Object,
            mfaRepo.Object,
            totpService.Object,
            deviceTrustService.Object,
            userRepository.Object,
            authSessionService.Object,
            TestRequestInfoFactory.Browser());
    }

    private static UserToken CreateUserToken(User user) =>
        new(
            new Token(
                "access-token",
                DateTime.UtcNow.AddMinutes(15),
                "refresh-token",
                "binding-token",
                TimeSpan.FromDays(1),
                SessionTransport.ApiToken),
            user);
}
