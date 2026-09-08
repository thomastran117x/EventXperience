using backend.main.application.security;
using backend.main.features.auth;
using backend.main.features.bloom;
using backend.main.features.auth.contracts;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa.totp;
using backend.main.features.auth.notifications;
using backend.main.features.auth.oauth;
using backend.main.features.auth.stepup;
using backend.main.features.auth.token;
using backend.main.features.cache;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;

using backend.tests.Unit.Support;

using FluentAssertions;

using Microsoft.Extensions.Configuration;

using Moq;
using Newtonsoft.Json;

namespace backend.tests.Unit.Features.Auth;

public class AuthServiceTests
{
    [Fact]
    public async Task LoginAsync_ShouldRejectUnknownUser()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetAuthByUsernameAsync("missing-user"))
            .ReturnsAsync((UserAuthRecord?)null);

        var service = CreateService(userRepository: userRepository);

        var act = () => service.LoginAsync(
            "missing-user",
            "Password123!",
            SessionTransport.BrowserCookie);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid username or password");
    }

    [Fact]
    public async Task HandleTokensAsync_ShouldRevokeSessionsForDisabledUsers()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(44))
            .ReturnsAsync(new TestUserBuilder().WithId(44).Disabled().Build());

        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.ValidateRefreshToken(
                "refresh-token",
                "binding-token",
                SessionTransport.BrowserCookie,
                It.IsAny<backend.main.shared.requests.ClientRequestInfo>()))
            .ReturnsAsync(new RefreshTokenValidationResult
            {
                SessionId = "session-44",
                UserId = 44,
                Transport = SessionTransport.BrowserCookie
            });

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications
        );

        var act = () => service.HandleTokensAsync(
            "refresh-token",
            "binding-token",
            SessionTransport.BrowserCookie);

        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("This account is disabled.");

        tokenService.Verify(s => s.RevokeAllRefreshSessionsAsync(44), Times.Once);
    }

    [Fact]
    public async Task SignUpAsync_ShouldNormalizeRoleBeforeCreatingVerificationArtifacts()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.EmailExistsAsync("new@example.com"))
            .ReturnsAsync(false);
        userRepository.Setup(repository => repository.GetAuthByUsernameAsync("new-user"))
            .ReturnsAsync((UserAuthRecord?)null);

        User? capturedUser = null;
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.GenerateVerificationArtifactsAsync(
                It.IsAny<backend.main.features.profile.User>(),
                VerificationPurpose.SignUp))
            .Callback<backend.main.features.profile.User, VerificationPurpose, bool>(
                (user, _, _) => capturedUser = user
            )
            .ReturnsAsync(new VerificationArtifacts
            {
                LinkToken = "verify-link",
                Purpose = VerificationPurpose.SignUp,
                OtpChallenge = new VerificationOtpChallenge
                {
                    Code = "123456",
                    Challenge = "challenge",
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                }
            });

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications);

        var result = await service.SignUpAsync(
            "new@example.com", "new-user", "Password123!", "organizer");

        result.Code.Should().Be("123456");
        capturedUser.Should().NotBeNull();
        capturedUser!.Usertype.Should().Be("Organizer");
        capturedUser.Username.Should().Be("new-user");
        notifications.Verify(service => service.SendSignupVerificationAsync(
            "new@example.com",
            "verify-link",
            "123456",
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task RecoverPasswordAsync_ShouldReturnPlaceholderWithoutPublishingForUnknownUsers()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByUsernameAsync("unknown-user"))
            .ReturnsAsync((UserRecoveryRecord?)null);

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(userRepository: userRepository, authNotificationService: notifications);

        var challenge = await service.RecoverPasswordAsync("unknown-user");

        challenge.Challenge.Should().NotBeNullOrWhiteSpace();
        challenge.Challenge.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        challenge.Code.Should().HaveLength(6);
        challenge.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        notifications.Verify(
            service => service.SendPasswordResetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RecoverPasswordAsync_ShouldSendProviderGuidance_ForProviderOnlyAccount()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByUsernameAsync("social-user"))
            .ReturnsAsync(new UserRecoveryRecord
            {
                Id = 9,
                Email = "social@example.com",
                Username = "social-user",
                HasLocalPassword = false,
                HasGoogleProvider = true
            });
        var notifications = new Mock<IAuthNotificationService>();
        var tokenService = new Mock<ITokenService>();
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications
        );

        var challenge = await service.RecoverPasswordAsync("social-user");

        challenge.Challenge.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        notifications.Verify(service => service.SendProviderSignInReminderAsync(
            "social@example.com",
            It.Is<IReadOnlyList<string>>(providers => providers.SequenceEqual(new[] { "Google" })),
            null
        ), Times.Once);
        tokenService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverUsernameAsync_ShouldSendUsername_ForLocalAccount()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByEmailAsync("member@example.com"))
            .ReturnsAsync(new UserRecoveryRecord
            {
                Id = 10,
                Email = "member@example.com",
                Username = "member-user",
                RecipientName = "Member",
                HasLocalPassword = true
            });
        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            authNotificationService: notifications
        );

        await service.RecoverUsernameAsync("member@example.com");

        notifications.Verify(service => service.SendUsernameReminderAsync(
            "member@example.com",
            "member-user",
            "Member"
        ), Times.Once);
    }

    [Fact]
    public async Task RecoverUsernameAsync_ShouldNotPublish_ForDisabledAccount()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByEmailAsync("disabled@example.com"))
            .ReturnsAsync(new UserRecoveryRecord
            {
                Id = 11,
                Email = "disabled@example.com",
                Username = "disabled-user",
                HasLocalPassword = true,
                IsDisabled = true
            });
        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            authNotificationService: notifications
        );

        await service.RecoverUsernameAsync("disabled@example.com");

        notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoginAsync_ShouldRequireStepUp_WhenTotpEnrolledAndSmsEnforcementIsDisabled()
    {
        using var scope = new TemporaryEnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["AUTH_SMS_MFA_ENFORCEMENT_ENABLED"] = "false",
            ["AUTH_TOTP_MFA_STEP_UP_ENABLED"] = "true"
        });

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetAuthByUsernameAsync("totp-user"))
            .ReturnsAsync(new UserAuthRecord
            {
                Id = 27,
                Email = "totp@example.com",
                Password = BCrypt.Net.BCrypt.HashPassword("Password123!", workFactor: 4),
                Usertype = "Participant",
                IsDisabled = false,
                AuthVersion = 1
            });

        var totpService = new Mock<ITotpMfaEnrollmentService>();
        totpService.Setup(service => service.GetEnrollmentAsync(27))
            .ReturnsAsync(new TotpMfaEnrollment
            {
                UserId = 27,
                EncryptedSecret = "v1:encrypted",
                IsTotpMfaEnabled = true
            });

        var deviceService = new Mock<IDeviceService>();
        var challengeService = new Mock<ILoginStepUpChallengeService>();
        challengeService.Setup(service => service.CreateChallengeAsync(
                It.Is<User>(user => user.Id == 27),
                SessionTransport.BrowserCookie,
                false,
                "/security"))
            .ReturnsAsync(new LoginStepUpChallengeResponse
            {
                Challenge = "stepup-challenge",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
                AvailableMethods = ["totp", "email"],
                MaskedEmail = "t***@example.com"
            });

        var service = CreateService(
            userRepository: userRepository,
            deviceService: deviceService,
            loginStepUpChallengeService: challengeService,
            totpMfaEnrollmentService: totpService);

        var result = await service.LoginAsync(
            "totp-user",
            "Password123!",
            SessionTransport.BrowserCookie,
            returnUrl: "/security");

        result.Type.Should().Be(AuthFlowResponseTypes.RequiresStepUp);
        result.StepUp.Should().NotBeNull();
        result.StepUp!.Challenge.Should().Be("stepup-challenge");
        deviceService.Verify(service => service.EnsureDeviceKnownAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<ClientRequestInfo>(), It.IsAny<string?>()), Times.Never);
        challengeService.VerifyAll();
    }
    [Fact]
    public async Task GoogleAsync_ShouldAttachProviderIdForExistingEmailUser()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-1"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("existing@example.com"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 9,
                Email = "existing@example.com",
                Usertype = "Participant",
                GoogleID = null,
                MicrosoftID = null,
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.UpdateProviderIdsAsync(9, "google-1", null))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 9,
                Email = "existing@example.com",
                Usertype = "Participant",
                GoogleID = "google-1",
                MicrosoftID = null,
                AuthVersion = 1
            });

        var oauthService = new Mock<IOAuthService>();
        oauthService.Setup(service => service.VerifyGoogleTokenAsync("google-token", null))
            .ReturnsAsync(new OAuthUser("google-1", "existing@example.com", "Existing User", "google"));

        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.GenerateAccessToken(It.IsAny<backend.main.features.profile.User>(), It.IsAny<string>()))
            .Returns(new AccessTokenIssue("access-token", DateTime.UtcNow.AddMinutes(15)));
        tokenService.Setup(service => service.GenerateRefreshToken(
                9,
                It.IsAny<backend.main.shared.requests.ClientRequestInfo>(),
                SessionTransport.BrowserCookie,
                null,
                null))
            .ReturnsAsync(new RefreshTokenIssue(
                "refresh-token",
                "binding-token",
                TimeSpan.FromDays(1),
                SessionTransport.BrowserCookie,
                "session-9"));

        var deviceService = new Mock<IDeviceService>();
        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 9,
            Email = "existing@example.com",
            Usertype = "Participant",
            GoogleID = "google-1"
        });
        var service = CreateService(
            userRepository: userRepository,
            oauthService: oauthService,
            tokenService: tokenService,
            deviceService: deviceService,
            authSessionService: authSessionService);

        var result = await service.GoogleAsync("google-token", SessionTransport.BrowserCookie);

        result.RequiresRoleSelection.Should().BeFalse();
        result.UserToken.Should().NotBeNull();
        result.UserToken!.user.GoogleID.Should().Be("google-1");
        userRepository.Verify(repository => repository.UpdateProviderIdsAsync(9, "google-1", null), Times.Once);
        deviceService.Verify(s => s.EnsureDeviceKnownAsync(9, "existing@example.com", It.IsAny<backend.main.shared.requests.ClientRequestInfo>(), It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// The provider id wins when the two lookups disagree.
    /// </summary>
    /// <remarks>
    /// This used to throw a conflict, which was safe only while an account's address could never
    /// diverge from the address its provider reports — linking only ever happened on an email
    /// match. Email changes make divergence ordinary: the account keeps the provider link, the
    /// provider keeps reporting the old address, and the moment anyone claims that released
    /// address the two lookups disagree forever. Throwing there would permanently lock out an
    /// account whose only sign-in method this is. Signing in as the provider-linked account is
    /// also simply the right answer: the caller proved control of that provider identity, and the
    /// other account merely happens to hold an address the provider still remembers.
    /// </remarks>
    [Fact]
    public async Task GoogleAsync_ShouldPreferTheProviderLink_WhenAnotherAccountHoldsThatAddress()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-1"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 1,
                Email = "provider@example.com",
                Usertype = "Participant",
                GoogleID = "google-1",
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("existing@example.com"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 2,
                Email = "existing@example.com",
                Usertype = "Participant",
                AuthVersion = 1
            });

        var oauthService = new Mock<IOAuthService>();
        oauthService.Setup(service => service.VerifyGoogleTokenAsync("google-token", null))
            .ReturnsAsync(new OAuthUser("google-1", "existing@example.com", "Existing User", "google"));

        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 1,
            Email = "provider@example.com",
            Usertype = "Participant",
            GoogleID = "google-1"
        });
        var service = CreateService(
            userRepository: userRepository,
            oauthService: oauthService,
            authSessionService: authSessionService);

        var result = await service.GoogleAsync("google-token", SessionTransport.BrowserCookie);

        result.UserToken.Should().NotBeNull();
        result.UserToken!.user.Id.Should().Be(1);
        result.UserToken.user.Email.Should().Be("provider@example.com");

        // The account holding the address is left entirely alone.
        userRepository.Verify(
            repository => repository.UpdateProviderIdsAsync(2, It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task VerifyAsync_ShouldCreateUser_AndReturnTokenPair()
    {
        var user = new backend.main.features.profile.User
        {
            Id = 12,
            Email = "verify@example.com",
            Username = "verify-user",
            Usertype = "Participant",
            AuthVersion = 1
        };

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.EmailExistsAsync(user.Email))
            .ReturnsAsync(false);
        userRepository.Setup(repository => repository.CreateUserAsync(It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(user);

        var tokenService = CreateTokenServiceForUser(user);
        tokenService.Setup(service => service.VerifyVerificationToken("verify-token", VerificationPurpose.SignUp))
            .ReturnsAsync(user);

        var authSessionService = CreateAuthSessionServiceForUser(user);
        var service = CreateService(userRepository: userRepository, tokenService: tokenService, authSessionService: authSessionService);

        var result = await service.VerifyAsync("verify-token", SessionTransport.BrowserCookie);

        result.user.Email.Should().Be(user.Email);
        userRepository.Verify(repository => repository.CreateUserAsync(It.Is<backend.main.features.profile.User>(u => u.Email == user.Email)), Times.Once);
    }

    [Fact]
    public async Task VerifyOtpAsync_ShouldCreateUser_AndReturnTokenPair()
    {
        var user = new backend.main.features.profile.User
        {
            Id = 13,
            Email = "verify-otp@example.com",
            Username = "verify-otp-user",
            Usertype = "Organizer",
            AuthVersion = 1
        };

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.EmailExistsAsync(user.Email))
            .ReturnsAsync(false);
        userRepository.Setup(repository => repository.CreateUserAsync(It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(user);

        var tokenService = CreateTokenServiceForUser(user);
        tokenService.Setup(service => service.VerifyVerificationOtpAsync("123456", "challenge", VerificationPurpose.SignUp))
            .ReturnsAsync(user);

        var authSessionService = CreateAuthSessionServiceForUser(user);
        var service = CreateService(userRepository: userRepository, tokenService: tokenService, authSessionService: authSessionService);

        var result = await service.VerifyOtpAsync("123456", "challenge", SessionTransport.BrowserCookie);

        result.user.Email.Should().Be(user.Email);
        userRepository.Verify(repository => repository.CreateUserAsync(It.Is<backend.main.features.profile.User>(u => u.Email == user.Email)), Times.Once);
    }

    [Fact]
    public async Task RecoverPasswordAsync_ShouldPublishResetMessage_ForActiveUser()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByUsernameAsync("active-user"))
            .ReturnsAsync(new UserRecoveryRecord
            {
                Id = 8,
                Email = "active@example.com",
                IsDisabled = false,
                HasLocalPassword = true
            });

        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.GenerateVerificationArtifactsAsync(
                It.IsAny<backend.main.features.profile.User>(),
                VerificationPurpose.ResetPassword,
                true))
            .ReturnsAsync(new VerificationArtifacts
            {
                LinkToken = "reset-link",
                Purpose = VerificationPurpose.ResetPassword,
                OtpChallenge = new VerificationOtpChallenge
                {
                    Code = "654321",
                    Challenge = "reset-challenge",
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
                }
            });

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(userRepository: userRepository, tokenService: tokenService, authNotificationService: notifications);

        var result = await service.RecoverPasswordAsync("active-user");

        result.Code.Should().Be("654321");
        notifications.Verify(service => service.SendPasswordResetAsync(
            "active@example.com",
            "reset-link",
            "654321",
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ResetPasswordAsync_ShouldUpdatePassword_IncrementAuthVersion_AndRevokeSessions()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetAuthByEmailAsync("reset@example.com"))
            .ReturnsAsync(new UserAuthRecord
            {
                Id = 21,
                Email = "reset@example.com",
                Password = "old-hash",
                Usertype = "Participant",
                IsDisabled = false,
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.UpdateUserAsync(21, It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 21,
                Email = "reset@example.com",
                Usertype = "Participant"
            });
        userRepository.Setup(repository => repository.IncrementAuthVersionAsync(21))
            .ReturnsAsync(true);

        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.VerifyVerificationToken("reset-token", VerificationPurpose.ResetPassword))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Email = "reset@example.com",
                Usertype = "Participant"
            });
        tokenService.Setup(service => service.RevokeAllRefreshSessionsAsync(21))
            .Returns(Task.CompletedTask);

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications
        );

        await service.ResetPasswordAsync("reset-token", "Password123!");

        userRepository.Verify(repository => repository.UpdateUserAsync(21, It.Is<backend.main.features.profile.User>(u =>
            u.Email == "reset@example.com" && !string.IsNullOrWhiteSpace(u.Password))), Times.Once);
        userRepository.Verify(repository => repository.IncrementAuthVersionAsync(21), Times.Once);
        tokenService.Verify(service => service.RevokeAllRefreshSessionsAsync(21), Times.Once);
        notifications.Verify(service => service.SendPasswordChangedAsync(
            "reset@example.com",
            null
        ), Times.Once);
    }

    [Fact]
    public async Task ResetPasswordWithOtpAsync_ShouldUpdatePassword_IncrementAuthVersion_AndRevokeSessions()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetAuthByEmailAsync("reset-otp@example.com"))
            .ReturnsAsync(new UserAuthRecord
            {
                Id = 22,
                Email = "reset-otp@example.com",
                Password = "old-hash",
                Usertype = "Participant",
                IsDisabled = false,
                AuthVersion = 3
            });
        userRepository.Setup(repository => repository.UpdateUserAsync(22, It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 22,
                Email = "reset-otp@example.com",
                Usertype = "Participant"
            });
        userRepository.Setup(repository => repository.IncrementAuthVersionAsync(22))
            .ReturnsAsync(true);

        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.VerifyVerificationOtpAsync("123456", "otp-challenge", VerificationPurpose.ResetPassword))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Email = "reset-otp@example.com",
                Usertype = "Participant"
            });
        tokenService.Setup(service => service.RevokeAllRefreshSessionsAsync(22))
            .Returns(Task.CompletedTask);

        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications
        );

        await service.ResetPasswordWithOtpAsync("123456", "otp-challenge", "Password123!");

        userRepository.Verify(repository => repository.UpdateUserAsync(22, It.Is<backend.main.features.profile.User>(u =>
            u.Email == "reset-otp@example.com" && !string.IsNullOrWhiteSpace(u.Password))), Times.Once);
        userRepository.Verify(repository => repository.IncrementAuthVersionAsync(22), Times.Once);
        tokenService.Verify(service => service.RevokeAllRefreshSessionsAsync(22), Times.Once);
        notifications.Verify(service => service.SendPasswordChangedAsync(
            "reset-otp@example.com",
            null
        ), Times.Once);
    }

    [Fact]
    public async Task MicrosoftAsync_ShouldReturnPendingSignup_WhenNoExistingUser()
    {
        var oauthService = new Mock<IOAuthService>();
        oauthService.Setup(service => service.VerifyMicrosoftTokenAsync("ms-token", null))
            .ReturnsAsync(new OAuthUser("ms-1", "new@example.com", "New User", "microsoft"));

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByMicrosoftIdAsync("ms-1"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("new@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);

        var cacheStore = new Dictionary<string, string>();
        var cache = new Mock<ICacheService>();
        cache.Setup(service => service.SetValueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Callback<string, string, TimeSpan?>((key, value, _) => cacheStore[key] = value)
            .ReturnsAsync(true);
        cache.Setup(service => service.GetValueAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => cacheStore.TryGetValue(key, out var value) ? value : null);

        var service = CreateService(userRepository: userRepository, oauthService: oauthService, cacheService: cache);

        var result = await service.MicrosoftAsync("ms-token", SessionTransport.BrowserCookie);

        result.RequiresRoleSelection.Should().BeTrue();
        result.PendingSignup.Should().NotBeNull();
        result.PendingSignup!.Email.Should().Be("new@example.com");
        cacheStore.Keys.Should().ContainSingle(key => key.StartsWith("oauth:pending:"));
    }

    [Fact]
    public async Task MicrosoftAsync_ShouldAuthenticateExistingUser_AndAssignDefaultOAuthRole()
    {
        var oauthService = new Mock<IOAuthService>();
        oauthService.Setup(service => service.VerifyMicrosoftTokenAsync("ms-token", null))
            .ReturnsAsync(new OAuthUser("ms-2", "member@example.com", "Member User", "microsoft"));

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByMicrosoftIdAsync("ms-2"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 15,
                Email = "member@example.com",
                Usertype = "MysteryRole",
                MicrosoftID = "ms-2",
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("member@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.UpdateUserAsync(15, It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 15,
                Email = "member@example.com",
                Usertype = AuthRoles.DefaultOAuthRole,
                MicrosoftID = "ms-2",
                AuthVersion = 1
            });

        var tokenService = CreateTokenServiceForUser(new backend.main.features.profile.User
        {
            Id = 15,
            Email = "member@example.com",
            Usertype = AuthRoles.DefaultOAuthRole,
            MicrosoftID = "ms-2",
            AuthVersion = 1
        });

        var deviceService = new Mock<IDeviceService>();
        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 15,
            Email = "member@example.com",
            Usertype = AuthRoles.DefaultOAuthRole,
            MicrosoftID = "ms-2",
            AuthVersion = 1
        });
        var service = CreateService(
            userRepository: userRepository,
            oauthService: oauthService,
            tokenService: tokenService,
            deviceService: deviceService,
            authSessionService: authSessionService);

        var result = await service.MicrosoftAsync("ms-token", SessionTransport.BrowserCookie);

        result.RequiresRoleSelection.Should().BeFalse();
        result.UserToken.Should().NotBeNull();
        result.UserToken!.user.Usertype.Should().Be(AuthRoles.DefaultOAuthRole);
        userRepository.Verify(repository => repository.UpdateUserAsync(15, It.Is<backend.main.features.profile.User>(u =>
            u.Usertype == AuthRoles.DefaultOAuthRole)), Times.Once);
        deviceService.Verify(service => service.EnsureDeviceKnownAsync(15, "member@example.com", It.IsAny<ClientRequestInfo>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task GoogleCodeAsync_ShouldExchangeCode_AndAuthenticateUser()
    {
        var oauthService = new Mock<IOAuthService>();
        oauthService.Setup(service => service.ExchangeGoogleCodeAsync("auth-code", "verifier", "https://app.test/callback"))
            .ReturnsAsync("google-id-token");
        oauthService.Setup(service => service.VerifyGoogleTokenAsync("google-id-token", "nonce-123"))
            .ReturnsAsync(new OAuthUser("google-55", "code@example.com", "Code User", "google"));

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-55"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 55,
                Email = "code@example.com",
                Usertype = "Participant",
                GoogleID = "google-55",
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("code@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);

        var tokenService = CreateTokenServiceForUser(new backend.main.features.profile.User
        {
            Id = 55,
            Email = "code@example.com",
            Usertype = "Participant",
            GoogleID = "google-55",
            AuthVersion = 1
        });

        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 55,
            Email = "code@example.com",
            Usertype = "Participant",
            GoogleID = "google-55",
            AuthVersion = 1
        });
        var service = CreateService(
            userRepository: userRepository,
            oauthService: oauthService,
            tokenService: tokenService,
            deviceService: new Mock<IDeviceService>(),
            authSessionService: authSessionService);

        var result = await service.GoogleCodeAsync(
            "auth-code",
            "verifier",
            "https://app.test/callback",
            SessionTransport.BrowserCookie,
            "nonce-123");

        result.RequiresRoleSelection.Should().BeFalse();
        result.UserToken.Should().NotBeNull();
        result.UserToken!.user.Email.Should().Be("code@example.com");
    }

    [Fact]
    public async Task CompleteOAuthSignupAsync_ShouldCreateNewUser_FromPendingSignup()
    {
        var signupToken = "signup-token";
        var pendingJson = JsonConvert.SerializeObject(new
        {
            ProviderUserId = "google-42",
            Email = "pending@example.com",
            Name = "Pending User",
            Provider = "google",
            Transport = SessionTransport.BrowserCookie
        });

        var cache = new Mock<ICacheService>();
        cache.Setup(service => service.GetValueAsync("oauth:pending:signup-token"))
            .ReturnsAsync(pendingJson);
        cache.Setup(service => service.DeleteKeyAsync("oauth:pending:signup-token"))
            .ReturnsAsync(true);

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-42"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("pending@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.CreateUserAsync(It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 77,
                Email = "pending@example.com",
                Usertype = "Organizer",
                GoogleID = "google-42",
                AuthVersion = 1
            });

        var tokenService = CreateTokenServiceForUser(new backend.main.features.profile.User
        {
            Id = 77,
            Email = "pending@example.com",
            Usertype = "Organizer",
            GoogleID = "google-42",
            AuthVersion = 1
        });

        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 77,
            Email = "pending@example.com",
            Usertype = "Organizer",
            GoogleID = "google-42",
            AuthVersion = 1
        });
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            cacheService: cache,
            authSessionService: authSessionService);

        var result = await service.CompleteOAuthSignupAsync(signupToken, "organizer", SessionTransport.BrowserCookie);

        result.user.Email.Should().Be("pending@example.com");
        userRepository.Verify(repository => repository.CreateUserAsync(It.Is<backend.main.features.profile.User>(u =>
            u.Email == "pending@example.com" && u.GoogleID == "google-42" && u.Usertype == "Organizer")), Times.Once);
        cache.Verify(service => service.DeleteKeyAsync("oauth:pending:signup-token"), Times.Once);
    }

    [Fact]
    public async Task CompleteOAuthSignupAsync_ShouldRejectTransportMismatch()
    {
        var pendingJson = JsonConvert.SerializeObject(new
        {
            ProviderUserId = "google-42",
            Email = "pending@example.com",
            Name = "Pending User",
            Provider = "google",
            Transport = SessionTransport.BrowserCookie
        });

        var cache = new Mock<ICacheService>();
        cache.Setup(service => service.GetValueAsync("oauth:pending:signup-token"))
            .ReturnsAsync(pendingJson);

        var service = CreateService(cacheService: cache);

        var act = () => service.CompleteOAuthSignupAsync(
            "signup-token",
            "organizer",
            SessionTransport.ApiToken);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("OAuth signup transport mismatch.");
    }

    [Fact]
    public async Task CompleteOAuthSignupAsync_ShouldNormalizeExistingUnknownRole()
    {
        var pendingJson = JsonConvert.SerializeObject(new
        {
            ProviderUserId = "google-84",
            Email = "existing@example.com",
            Name = "Existing User",
            Provider = "google",
            Transport = SessionTransport.BrowserCookie
        });

        var cache = new Mock<ICacheService>();
        cache.Setup(service => service.GetValueAsync("oauth:pending:signup-token"))
            .ReturnsAsync(pendingJson);
        cache.Setup(service => service.DeleteKeyAsync("oauth:pending:signup-token"))
            .ReturnsAsync(true);

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-84"))
            .ReturnsAsync(new UserOAuthRecord
            {
                Id = 84,
                Email = "existing@example.com",
                Usertype = "UnknownRole",
                GoogleID = "google-84",
                AuthVersion = 1
            });
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("existing@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.UpdateUserAsync(84, It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 84,
                Email = "existing@example.com",
                Usertype = AuthRoles.DefaultOAuthRole,
                GoogleID = "google-84",
                AuthVersion = 1
            });

        var tokenService = CreateTokenServiceForUser(new backend.main.features.profile.User
        {
            Id = 84,
            Email = "existing@example.com",
            Usertype = AuthRoles.DefaultOAuthRole,
            GoogleID = "google-84",
            AuthVersion = 1
        });

        var authSessionService = CreateAuthSessionServiceForUser(new backend.main.features.profile.User
        {
            Id = 84,
            Email = "existing@example.com",
            Usertype = AuthRoles.DefaultOAuthRole,
            GoogleID = "google-84",
            AuthVersion = 1
        });
        var service = CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            cacheService: cache,
            authSessionService: authSessionService);

        var result = await service.CompleteOAuthSignupAsync(
            "signup-token",
            "participant",
            SessionTransport.BrowserCookie);

        result.user.Usertype.Should().Be(AuthRoles.DefaultOAuthRole);
        userRepository.Verify(repository => repository.UpdateUserAsync(84, It.Is<backend.main.features.profile.User>(u =>
            u.Usertype == AuthRoles.DefaultOAuthRole)), Times.Once);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ShouldReturnEnabledUser()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(51))
            .ReturnsAsync(new backend.main.features.profile.User
            {
                Id = 51,
                Email = "current@example.com",
                Usertype = "Volunteer",
                IsDisabled = false
            });

        var service = CreateService(userRepository: userRepository);

        var result = await service.GetCurrentUserAsync(51);

        result.Email.Should().Be("current@example.com");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ShouldThrowWhenUserIsMissing()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(404))
            .ReturnsAsync((backend.main.features.profile.User?)null);

        var service = CreateService(userRepository: userRepository);

        var act = () => service.GetCurrentUserAsync(404);

        await act.Should().ThrowAsync<ResourceNotFoundException>()
            .WithMessage("User with ID 404 is not found");
    }

    [Fact]
    public async Task GetCurrentUserAsync_ShouldThrowWhenUserIsDisabled()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(12))
            .ReturnsAsync(new TestUserBuilder()
                .WithId(12)
                .WithEmail("disabled@example.com")
                .Disabled()
                .Build());

        var service = CreateService(userRepository: userRepository);

        var act = () => service.GetCurrentUserAsync(12);

        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("This account is disabled.");
    }

    [Fact]
    public async Task HandleTokensAsync_ShouldReturnRotatedTokenPair()
    {
        var user = new TestUserBuilder()
            .WithId(63)
            .WithEmail("refresh@example.com")
            .Build();

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetUserAsync(63))
            .ReturnsAsync(user);

        var tokenService = CreateTokenServiceForUser(user);
        tokenService.Setup(service => service.ValidateRefreshToken(
                "old-refresh-token",
                "binding-token",
                SessionTransport.BrowserCookie,
                It.IsAny<ClientRequestInfo>()))
            .ReturnsAsync(new RefreshTokenValidationResult
            {
                SessionId = "existing-session",
                UserId = 63,
                Transport = SessionTransport.BrowserCookie
            });

        var authSessionService = CreateAuthSessionServiceForUser(user);
        var service = CreateService(userRepository: userRepository, tokenService: tokenService, authSessionService: authSessionService);

        var result = await service.HandleTokensAsync(
            "old-refresh-token",
            "binding-token",
            SessionTransport.BrowserCookie);

        result.user.Email.Should().Be("refresh@example.com");
        result.token.RefreshToken.Should().Be("refresh-token");
    }

    [Fact]
    public async Task VerifyDeviceLoginAsync_ShouldReturnDeviceIssuedToken()
    {
        var expectedToken = new UserToken(
            new Token(
                "access-token",
                DateTime.UtcNow.AddMinutes(15),
                "refresh-token",
                "binding-token",
                TimeSpan.FromDays(1),
                SessionTransport.BrowserCookie),
            new TestUserBuilder().WithId(90).WithEmail("device@example.com").Build());
        var expected = new AuthenticatedSessionResult { UserToken = expectedToken };

        var deviceService = new Mock<IDeviceService>();
        deviceService.Setup(service => service.VerifyDeviceAsync("device-token", SessionTransport.BrowserCookie))
            .ReturnsAsync(expected);

        var service = CreateService(deviceService: deviceService);

        var result = await service.VerifyDeviceLoginAsync("device-token", SessionTransport.BrowserCookie);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task HandleLogoutAsync_ShouldValidateAndRevokeRefreshSession()
    {
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.ValidateRefreshToken(
                "refresh-token",
                "binding-token",
                SessionTransport.BrowserCookie,
                It.IsAny<ClientRequestInfo>()))
            .ReturnsAsync(new RefreshTokenValidationResult
            {
                SessionId = "session-9",
                UserId = 9,
                Transport = SessionTransport.BrowserCookie
            });
        tokenService.Setup(service => service.RevokeRefreshSessionAsync("session-9"))
            .Returns(Task.CompletedTask);

        var service = CreateService(tokenService: tokenService);

        await service.HandleLogoutAsync("refresh-token", "binding-token", SessionTransport.BrowserCookie);

        tokenService.Verify(service => service.RevokeRefreshSessionAsync("session-9"), Times.Once);
    }

    /// <summary>
    /// The address we persist and deliver mail to must keep the casing the user typed.
    /// </summary>
    /// <remarks>
    /// RFC 5321 leaves the local part case-sensitive to the destination host, so mailing a
    /// lowercased address can bounce on a host that distinguishes them — and the account would be
    /// unrecoverable, because the stored address is not one that exists. Only the filter lookup
    /// gets the lowercased form.
    /// </remarks>
    [Fact]
    public async Task SignUpAsync_ShouldMailTheAddressAsTyped_WhileProbingTheNormalisedForm()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateServiceWithEmailFilter(
            emailAvailability,
            out var userRepository,
            out var notifications,
            out var tokenService);
        userRepository.Setup(repository => repository.GetAuthByUsernameAsync("new-user"))
            .ReturnsAsync((UserAuthRecord?)null);

        backend.main.features.profile.User? captured = null;
        tokenService.Setup(t => t.GenerateVerificationArtifactsAsync(
                It.IsAny<backend.main.features.profile.User>(),
                VerificationPurpose.SignUp))
            .Callback<backend.main.features.profile.User, VerificationPurpose, bool>(
                (user, _, _) => captured = user)
            .ReturnsAsync(SignupArtifacts());

        await service.SignUpAsync("  Ada.Lovelace@Example.COM  ", "new-user", "Password123!", "organizer");

        // Trimmed, but not lowercased.
        captured.Should().NotBeNull();
        captured!.Email.Should().Be("Ada.Lovelace@Example.COM");
        notifications.Verify(n => n.SendSignupVerificationAsync(
                "Ada.Lovelace@Example.COM",
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once);

        // The filter only ever sees the lowercased form.
        emailAvailability.Verify(availability => availability.IsRegisteredAsync(
                "ada.lovelace@example.com",
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyAsync_ShouldPersistTheAddressAsTyped_WhileRecordingTheNormalisedForm()
    {
        var user = new backend.main.features.profile.User
        {
            Id = 12,
            Email = "  Ada.Lovelace@Example.COM  ",
            Username = "verify-user",
            Usertype = "Participant",
            AuthVersion = 1
        };
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateSignupCompletionService(user, emailAvailability, out var tokenService);
        tokenService.Setup(t => t.VerifyVerificationToken("verify-token", VerificationPurpose.SignUp))
            .ReturnsAsync(user);

        await service.VerifyAsync("verify-token", SessionTransport.BrowserCookie);

        user.Email.Should().Be("Ada.Lovelace@Example.COM");
        emailAvailability.Verify(availability => availability.MarkRegisteredAsync(
                "ada.lovelace@example.com",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The signup pre-flight creates nothing, and the verify step re-checks authoritatively before
    /// inserting, so this is the one email read allowed to trust the filter.
    /// </summary>
    [Fact]
    public async Task SignUpAsync_ShouldProbeTheEmailFilterAdvisorily()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateServiceWithEmailFilter(emailAvailability, out var userRepository);
        userRepository.Setup(repository => repository.GetAuthByUsernameAsync("new-user"))
            .ReturnsAsync((UserAuthRecord?)null);

        await service.SignUpAsync("New@Example.COM", "new-user", "Password123!", "organizer");

        emailAvailability.Verify(availability => availability.IsRegisteredAsync(
                "new@example.com",
                AvailabilityLookupMode.Advisory,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SignUpAsync_ShouldRejectAnAddressReportedAsRegistered()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        emailAvailability.Setup(availability => availability.IsRegisteredAsync(
                It.IsAny<string>(),
                It.IsAny<AvailabilityLookupMode>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateServiceWithEmailFilter(emailAvailability, out _);

        var act = () => service.SignUpAsync("taken@example.com", "new-user", "Password123!", "organizer");

        await act.Should().ThrowAsync<ConflictException>();
    }

    /// <summary>
    /// CreateUserAsync is a few lines away, so a stale "absent" here would turn a clean 409 into a
    /// unique-index violation surfacing as a 500.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_ShouldProbeTheEmailFilterAuthoritatively_AndRecordTheAddress()
    {
        var user = new backend.main.features.profile.User
        {
            Id = 12,
            Email = "Verify@Example.COM",
            Username = "verify-user",
            Usertype = "Participant",
            AuthVersion = 1
        };
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateSignupCompletionService(user, emailAvailability, out var tokenService);
        tokenService.Setup(t => t.VerifyVerificationToken("verify-token", VerificationPurpose.SignUp))
            .ReturnsAsync(user);

        await service.VerifyAsync("verify-token", SessionTransport.BrowserCookie);

        emailAvailability.Verify(availability => availability.IsRegisteredAsync(
                "verify@example.com",
                AvailabilityLookupMode.Authoritative,
                It.IsAny<CancellationToken>()),
            Times.Once);
        emailAvailability.Verify(
            availability => availability.MarkRegisteredAsync("verify@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyOtpAsync_ShouldProbeTheEmailFilterAuthoritatively_AndRecordTheAddress()
    {
        var user = new backend.main.features.profile.User
        {
            Id = 13,
            Email = "verify-otp@example.com",
            Username = "verify-otp-user",
            Usertype = "Organizer",
            AuthVersion = 1
        };
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateSignupCompletionService(user, emailAvailability, out var tokenService);
        tokenService.Setup(t => t.VerifyVerificationOtpAsync("123456", "challenge", VerificationPurpose.SignUp))
            .ReturnsAsync(user);

        await service.VerifyOtpAsync("123456", "challenge", SessionTransport.BrowserCookie);

        emailAvailability.Verify(availability => availability.IsRegisteredAsync(
                "verify-otp@example.com",
                AvailabilityLookupMode.Authoritative,
                It.IsAny<CancellationToken>()),
            Times.Once);
        emailAvailability.Verify(
            availability => availability.MarkRegisteredAsync("verify-otp@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// OAuth accounts have no username, so this is the only filter write the path makes. Without
    /// it a provider-created address keeps reporting as free until the next scheduled rebuild.
    /// </summary>
    [Fact]
    public async Task CompleteOAuthSignupAsync_ShouldRecordTheAddress_ForANewAccount()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateOAuthSignupService(emailAvailability, existingUser: false);

        await service.CompleteOAuthSignupAsync("signup-token", "organizer", SessionTransport.BrowserCookie);

        emailAvailability.Verify(
            availability => availability.MarkRegisteredAsync("pending@example.com", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Linking a provider to an account that already exists creates no row, so there is nothing
    /// new for the filter to learn.
    /// </summary>
    [Fact]
    public async Task CompleteOAuthSignupAsync_ShouldNotRecordTheAddress_WhenLinkingAnExistingAccount()
    {
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var service = CreateOAuthSignupService(emailAvailability, existingUser: true);

        await service.CompleteOAuthSignupAsync("signup-token", "organizer", SessionTransport.BrowserCookie);

        emailAvailability.Verify(
            availability => availability.MarkRegisteredAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The saving this buys: an address no account has ever used costs a bitmap probe instead of
    /// a query.
    /// </summary>
    [Fact]
    public async Task RecoverUsernameAsync_ShouldSkipTheQuery_WhenTheFilterProvesTheAddressIsUnknown()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        emailAvailability.Setup(availability => availability.IsDefinitelyUnregistered("stranger@example.com"))
            .Returns(true);
        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            authNotificationService: notifications,
            emailAvailability: emailAvailability.Object);

        await service.RecoverUsernameAsync("  Stranger@Example.COM  ");

        userRepository.Verify(
            repository => repository.GetRecoveryByEmailAsync(It.IsAny<string>()),
            Times.Never);
        notifications.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The filter-only check must never add a round trip: when it cannot prove absence, this path
    /// does exactly the one query it always did.
    /// </summary>
    [Fact]
    public async Task RecoverUsernameAsync_ShouldQueryOnce_WhenTheFilterCannotProveAbsence()
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetRecoveryByEmailAsync("member@example.com"))
            .ReturnsAsync(new UserRecoveryRecord
            {
                Id = 10,
                Email = "member@example.com",
                Username = "member-user",
                RecipientName = "Member",
                HasLocalPassword = true
            });
        var emailAvailability = new Mock<IEmailAvailabilityService>();
        var notifications = new Mock<IAuthNotificationService>();
        var service = CreateService(
            userRepository: userRepository,
            authNotificationService: notifications,
            emailAvailability: emailAvailability.Object);

        await service.RecoverUsernameAsync("Member@Example.COM");

        userRepository.Verify(
            repository => repository.GetRecoveryByEmailAsync("member@example.com"),
            Times.Once);
        userRepository.Verify(repository => repository.EmailExistsAsync(It.IsAny<string>()), Times.Never);
        notifications.Verify(
            n => n.SendUsernameReminderAsync("member@example.com", "member-user", "Member"),
            Times.Once);
    }

    private static VerificationArtifacts SignupArtifacts() => new()
    {
        LinkToken = "verify-link",
        Purpose = VerificationPurpose.SignUp,
        OtpChallenge = new VerificationOtpChallenge
        {
            Code = "123456",
            Challenge = "challenge",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30)
        }
    };

    private static AuthService CreateServiceWithEmailFilter(
        Mock<IEmailAvailabilityService> emailAvailability,
        out Mock<IAuthUserRepository> userRepository) =>
        CreateServiceWithEmailFilter(emailAvailability, out userRepository, out _, out _);

    private static AuthService CreateServiceWithEmailFilter(
        Mock<IEmailAvailabilityService> emailAvailability,
        out Mock<IAuthUserRepository> userRepository,
        out Mock<IAuthNotificationService> notifications,
        out Mock<ITokenService> tokenService)
    {
        userRepository = new Mock<IAuthUserRepository>();
        notifications = new Mock<IAuthNotificationService>();
        tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.GenerateVerificationArtifactsAsync(
                It.IsAny<backend.main.features.profile.User>(),
                VerificationPurpose.SignUp))
            .ReturnsAsync(SignupArtifacts());

        return CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authNotificationService: notifications,
            emailAvailability: emailAvailability.Object);
    }

    private static AuthService CreateSignupCompletionService(
        backend.main.features.profile.User user,
        Mock<IEmailAvailabilityService> emailAvailability,
        out Mock<ITokenService> tokenService)
    {
        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.CreateUserAsync(It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(user);

        tokenService = CreateTokenServiceForUser(user);

        return CreateService(
            userRepository: userRepository,
            tokenService: tokenService,
            authSessionService: CreateAuthSessionServiceForUser(user),
            emailAvailability: emailAvailability.Object);
    }

    private static AuthService CreateOAuthSignupService(
        Mock<IEmailAvailabilityService> emailAvailability,
        bool existingUser)
    {
        var pendingJson = JsonConvert.SerializeObject(new
        {
            ProviderUserId = "google-42",
            Email = "pending@example.com",
            Name = "Pending User",
            Provider = "google",
            Transport = SessionTransport.BrowserCookie
        });

        var cache = new Mock<ICacheService>();
        cache.Setup(service => service.GetValueAsync("oauth:pending:signup-token"))
            .ReturnsAsync(pendingJson);
        cache.Setup(service => service.DeleteKeyAsync("oauth:pending:signup-token"))
            .ReturnsAsync(true);

        var user = new backend.main.features.profile.User
        {
            Id = 77,
            Email = "pending@example.com",
            Usertype = "Organizer",
            GoogleID = "google-42",
            AuthVersion = 1
        };

        var userRepository = new Mock<IAuthUserRepository>();
        userRepository.Setup(repository => repository.GetOAuthByGoogleIdAsync("google-42"))
            .ReturnsAsync(existingUser
                ? new UserOAuthRecord
                {
                    Id = 77,
                    Email = "pending@example.com",
                    Usertype = "Organizer",
                    GoogleID = "google-42",
                    AuthVersion = 1
                }
                : null);
        userRepository.Setup(repository => repository.GetOAuthByEmailAsync("pending@example.com"))
            .ReturnsAsync((UserOAuthRecord?)null);
        userRepository.Setup(repository => repository.CreateUserAsync(It.IsAny<backend.main.features.profile.User>()))
            .ReturnsAsync(user);

        return CreateService(
            userRepository: userRepository,
            tokenService: CreateTokenServiceForUser(user),
            cacheService: cache,
            authSessionService: CreateAuthSessionServiceForUser(user),
            emailAvailability: emailAvailability.Object);
    }

    private static AuthService CreateService(
        Mock<IAuthUserRepository>? userRepository = null,
        Mock<IOAuthService>? oauthService = null,
        Mock<ITokenService>? tokenService = null,
        Mock<IAuthNotificationService>? authNotificationService = null,
        Mock<IDeviceService>? deviceService = null,
        Mock<ITotpMfaEnrollmentService>? totpMfaEnrollmentService = null,
        Mock<ICacheService>? cacheService = null,
        Mock<IDeviceTrustService>? deviceTrustService = null,
        Mock<ILoginStepUpChallengeService>? loginStepUpChallengeService = null,
        Mock<IAuthSessionService>? authSessionService = null,
        SeedAccountBypassPolicy? seedBypass = null,
        IEmailAvailabilityService? emailAvailability = null)
    {
        userRepository ??= new Mock<IAuthUserRepository>();
        oauthService ??= new Mock<IOAuthService>();
        tokenService ??= new Mock<ITokenService>();
        authNotificationService ??= new Mock<IAuthNotificationService>();
        deviceService ??= new Mock<IDeviceService>();
        totpMfaEnrollmentService ??= new Mock<ITotpMfaEnrollmentService>();
        cacheService ??= new Mock<ICacheService>();
        deviceTrustService ??= new Mock<IDeviceTrustService>();
        loginStepUpChallengeService ??= new Mock<ILoginStepUpChallengeService>();
        authSessionService ??= new Mock<IAuthSessionService>();
        seedBypass ??= new SeedAccountBypassPolicy(new ConfigurationBuilder().Build());
        // Defaults to the real service over the disabled registry, so every existing test keeps
        // resolving emails against the repository exactly as it did before the filter existed.
        emailAvailability ??= new EmailAvailabilityService(
            userRepository.Object,
            new DisabledBloomFilterRegistry());

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
            emailAvailability,
            seedBypass,
            TestRequestInfoFactory.Browser());
    }

    private static Mock<ITokenService> CreateTokenServiceForUser(backend.main.features.profile.User user)
    {
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(service => service.GenerateAccessToken(It.IsAny<backend.main.features.profile.User>(), It.IsAny<string>()))
            .Returns(new AccessTokenIssue("access-token", DateTime.UtcNow.AddMinutes(15)));
        tokenService.Setup(service => service.GenerateRefreshToken(
                user.Id,
                It.IsAny<ClientRequestInfo>(),
                SessionTransport.BrowserCookie,
                It.IsAny<string?>(),
                It.IsAny<bool?>()))
            .ReturnsAsync(new RefreshTokenIssue(
                "refresh-token",
                "binding-token",
                TimeSpan.FromDays(1),
                SessionTransport.BrowserCookie,
                "session-" + user.Id));
        return tokenService;
    }

    private static Mock<IAuthSessionService> CreateAuthSessionServiceForUser(backend.main.features.profile.User user)
    {
        var authSessionService = new Mock<IAuthSessionService>();
        authSessionService.Setup(s => s.IssueAsync(
                It.Is<backend.main.features.profile.User>(u => u.Id == user.Id),
                It.IsAny<SessionTransport>(),
                It.IsAny<string?>(),
                It.IsAny<bool?>()))
            .ReturnsAsync(new UserToken(
                new Token(
                    "access-token",
                    DateTime.UtcNow.AddMinutes(15),
                    "refresh-token",
                    "binding-token",
                    TimeSpan.FromDays(1),
                    SessionTransport.BrowserCookie),
                user));
        return authSessionService;
    }

    private sealed class TemporaryEnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originals = [];

        public TemporaryEnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pair in values)
            {
                _originals[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _originals)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}



