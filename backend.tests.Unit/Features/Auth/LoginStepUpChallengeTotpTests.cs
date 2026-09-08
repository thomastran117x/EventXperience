using backend.main.features.auth;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.stepup;
using backend.main.features.auth.token;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;

using backend.tests.Unit.Support;

using FluentAssertions;

using Moq;

namespace backend.tests.Unit.Features.Auth;

public class LoginStepUpChallengeTotpTests
{
    [Fact]
    public async Task CreateChallengeAsync_ShouldIncludeTotpAndEmail_WhenTotpIsEnabled()
    {
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(repository => repository.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var totpService = new Mock<ITotpMfaEnrollmentService>();
        totpService.Setup(service => service.GetEnrollmentAsync(5))
            .ReturnsAsync(new TotpMfaEnrollment
            {
                UserId = 5,
                EncryptedSecret = "v1:encrypted",
                IsTotpMfaEnabled = true
            });

        var user = new TestUserBuilder().WithId(5).WithEmail("totp@example.com").Build();
        var service = CreateService(mfaRepo: mfaRepo, totpService: totpService);

        var response = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/dashboard");

        response.AvailableMethods.Should().ContainInOrder("totp", "email");
        response.MaskedPhone.Should().BeNull();
        response.MaskedEmail.Should().Be("t***@example.com");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnTotpMethodWithoutSendingNotifications()
    {
        var cache = new InMemoryCacheService();
        var notifications = new Mock<IAuthNotificationService>();
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(repository => repository.GetByUserIdAsync(5)).ReturnsAsync((SmsMfaEnrollment?)null);

        var totpService = new Mock<ITotpMfaEnrollmentService>();
        totpService.Setup(service => service.GetEnrollmentAsync(5))
            .ReturnsAsync(new TotpMfaEnrollment
            {
                UserId = 5,
                EncryptedSecret = "v1:encrypted",
                IsTotpMfaEnabled = true
            });

        var user = new TestUserBuilder().WithId(5).WithEmail("totp@example.com").Build();
        var service = CreateService(cache, notifications, mfaRepo, totpService);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/dashboard");
        var response = await service.StartAsync(challenge.Challenge, "totp");

        response.SelectedMethod.Should().Be("totp");
        response.Challenge.Should().Be(challenge.Challenge);
        response.MaskedDestination.Should().Be("authenticator app");
        notifications.Verify(service => service.SendSmsMfaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()), Times.Never);
        notifications.Verify(service => service.SendDeviceVerificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task VerifyTotpAsync_ShouldReturnAuthResult_WhenCodeIsValid()
    {
        var cache = new InMemoryCacheService();
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(repository => repository.GetByUserIdAsync(7)).ReturnsAsync((SmsMfaEnrollment?)null);

        var totpService = new Mock<ITotpMfaEnrollmentService>();
        totpService.Setup(service => service.GetEnrollmentAsync(7))
            .ReturnsAsync(new TotpMfaEnrollment
            {
                UserId = 7,
                EncryptedSecret = "v1:encrypted",
                IsTotpMfaEnabled = true
            });
        totpService.Setup(service => service.VerifyPersistedCodeAsync(7, "123456"))
            .Returns(Task.CompletedTask);

        var user = new TestUserBuilder().WithId(7).WithEmail("totp-stepup@example.com").Build();
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(7)).ReturnsAsync(user);

        var deviceTrustService = new Mock<IDeviceTrustService>();
        deviceTrustService.Setup(service => service.TrustAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var expectedToken = CreateUserToken(user);
        var authSessionService = new Mock<IAuthSessionService>();
        authSessionService.Setup(service => service.IssueAsync(It.IsAny<User>(), It.IsAny<SessionTransport>(), It.IsAny<string?>(), It.IsAny<bool?>()))
            .ReturnsAsync(expectedToken);

        var service = CreateService(
            cache: cache,
            mfaRepo: mfaRepo,
            totpService: totpService,
            deviceTrustService: deviceTrustService,
            userRepository: userRepository,
            authSessionService: authSessionService);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, "/bookings/9");
        var result = await service.VerifyTotpAsync(challenge.Challenge, "123456");

        result.UserToken.Should().Be(expectedToken);
        result.ReturnPath.Should().Be("/bookings/9");
        totpService.Verify(service => service.VerifyPersistedCodeAsync(7, "123456"), Times.Once);
    }

    [Fact]
    public async Task VerifyTotpAsync_ShouldExpireChallenge_AfterMaxFailedAttempts()
    {
        var cache = new InMemoryCacheService();
        var mfaRepo = new Mock<IMfaEnrollmentRepository>();
        mfaRepo.Setup(repository => repository.GetByUserIdAsync(8)).ReturnsAsync((SmsMfaEnrollment?)null);

        var totpService = new Mock<ITotpMfaEnrollmentService>();
        totpService.Setup(service => service.GetEnrollmentAsync(8))
            .ReturnsAsync(new TotpMfaEnrollment
            {
                UserId = 8,
                EncryptedSecret = "v1:encrypted",
                IsTotpMfaEnabled = true
            });
        totpService.Setup(service => service.VerifyPersistedCodeAsync(8, "000000"))
            .ThrowsAsync(new UnauthorizedException("Invalid or expired TOTP code."));

        var user = new TestUserBuilder().WithId(8).WithEmail("totp-failure@example.com").Build();
        var service = CreateService(cache: cache, mfaRepo: mfaRepo, totpService: totpService);

        var challenge = await service.CreateChallengeAsync(user, SessionTransport.ApiToken, false, null);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var act = () => service.VerifyTotpAsync(challenge.Challenge, "000000");
            await act.Should().ThrowAsync<UnauthorizedException>();
        }

        var afterMaxAttempts = () => service.VerifyTotpAsync(challenge.Challenge, "000000");
        await afterMaxAttempts.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired sign-in verification challenge.");
        totpService.Verify(service => service.VerifyPersistedCodeAsync(8, "000000"), Times.Exactly(5));
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
            new Mock<ITokenService>().Object,
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
