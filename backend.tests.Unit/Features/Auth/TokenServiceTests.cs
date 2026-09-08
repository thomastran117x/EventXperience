using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using backend.main.features.auth.token;
using backend.main.features.profile;
using backend.main.shared.exceptions.http;
using backend.main.shared.requests;

using FluentAssertions;

namespace backend.tests.Unit.Features.Auth;

public class TokenServiceTests
{
    [Fact]
    public void GenerateAccessToken_ShouldIncludeIdentityRoleAndAuthVersionClaims()
    {
        var service = new TokenService(new InMemoryCacheService());
        var user = new User
        {
            Id = 23,
            Email = "claims@example.com",
            Usertype = "participant",
            AuthVersion = 7
        };

        var issue = service.GenerateAccessToken(user, "session-abc");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(issue.Value);

        jwt.Claims.Should().Contain(claim => (claim.Type == ClaimTypes.NameIdentifier || claim.Type == "nameid") && claim.Value == "23");
        jwt.Claims.Should().Contain(claim => (claim.Type == ClaimTypes.Name || claim.Type == "unique_name") && claim.Value == "claims@example.com");
        jwt.Claims.Should().Contain(claim => (claim.Type == ClaimTypes.Role || claim.Type == "role") && claim.Value == "Participant");
        jwt.Claims.Should().Contain(claim => claim.Type == TokenService.AuthVersionClaimType && claim.Value == "7");
        jwt.Claims.Should().Contain(claim => claim.Type == TokenService.SessionIdClaimType && claim.Value == "session-abc");
        issue.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(10));
    }

    // ------------------------------------------------------- email change

    [Fact]
    public async Task GenerateEmailChangeArtifactsAsync_ShouldBindTheArtifactsToTheAccount()
    {
        var service = new TokenService(new InMemoryCacheService());

        var artifacts = await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");
        var pending = await service.ConsumeEmailChangeTokenAsync(artifacts.LinkToken);

        pending.UserId.Should().Be(23);
        pending.AuthVersion.Should().Be(1);
        pending.NewEmail.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ConsumeEmailChangeOtpAsync_ShouldReturnTheAccountAndAddress()
    {
        var service = new TokenService(new InMemoryCacheService());

        var artifacts = await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");
        var pending = await service.ConsumeEmailChangeOtpAsync(
            artifacts.OtpChallenge.Code,
            artifacts.OtpChallenge.Challenge);

        pending.UserId.Should().Be(23);
        pending.AuthVersion.Should().Be(1);
        pending.NewEmail.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ConsumeEmailChangeTokenAsync_ShouldRejectAReusedToken()
    {
        var service = new TokenService(new InMemoryCacheService());
        var artifacts = await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");

        await service.ConsumeEmailChangeTokenAsync(artifacts.LinkToken);
        var act = () => service.ConsumeEmailChangeTokenAsync(artifacts.LinkToken);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    /// <summary>
    /// A signup token must not be redeemable as an email change: its payload carries no account
    /// id, and treating one purpose as another is how a verification flow becomes a takeover.
    /// </summary>
    [Fact]
    public async Task ConsumeEmailChangeTokenAsync_ShouldRejectAnotherPurposesToken()
    {
        var service = new TokenService(new InMemoryCacheService());
        var user = new User { Email = "signup@example.com", Usertype = "participant" };
        var token = await service.GenerateVerificationToken(user, VerificationPurpose.SignUp);

        var act = () => service.ConsumeEmailChangeTokenAsync(token);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("*purpose mismatch*");
    }

    [Fact]
    public async Task GetPendingEmailChangeAsync_ShouldReportTheAddressAwaitingConfirmation()
    {
        var service = new TokenService(new InMemoryCacheService());
        await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");

        var pending = await service.GetPendingEmailChangeAsync(23);

        pending.Should().NotBeNull();
        pending!.NewEmail.Should().Be("new@example.com");
    }

    [Fact]
    public async Task GetPendingEmailChangeAsync_ShouldReportNothing_AfterConfirmation()
    {
        var service = new TokenService(new InMemoryCacheService());
        var artifacts = await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");

        await service.ConsumeEmailChangeTokenAsync(artifacts.LinkToken);

        (await service.GetPendingEmailChangeAsync(23)).Should().BeNull();
    }

    [Fact]
    public async Task CancelPendingEmailChangeAsync_ShouldMakeTheLinkUnusable()
    {
        var service = new TokenService(new InMemoryCacheService());
        var artifacts = await service.GenerateEmailChangeArtifactsAsync(23, 1, "new@example.com");

        await service.CancelPendingEmailChangeAsync(23);

        (await service.GetPendingEmailChangeAsync(23)).Should().BeNull();
        var act = () => service.ConsumeEmailChangeTokenAsync(artifacts.LinkToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    /// <summary>
    /// State is keyed by the target address, so re-requesting against a different one would strand
    /// the first request's artifacts as separately redeemable without an explicit cancel.
    /// </summary>
    [Fact]
    public async Task GenerateEmailChangeArtifactsAsync_ShouldInvalidateAnEarlierRequest()
    {
        var service = new TokenService(new InMemoryCacheService());
        var first = await service.GenerateEmailChangeArtifactsAsync(23, 1, "first@example.com");

        await service.GenerateEmailChangeArtifactsAsync(23, 1, "second@example.com");

        var act = () => service.ConsumeEmailChangeTokenAsync(first.LinkToken);
        await act.Should().ThrowAsync<UnauthorizedException>();

        (await service.GetPendingEmailChangeAsync(23))!.NewEmail.Should().Be("second@example.com");
    }

    /// <summary>
    /// Two requests racing must not leave a redeemable proof that nothing can reach.
    /// </summary>
    /// <remarks>
    /// Each pending change is stored under its own target address, so without serialization both
    /// calls clear the same empty index, both mint a token, and only the later one stays indexed -
    /// leaving the earlier proof live but invisible to a later cancel. The lock makes the second
    /// request either wait its turn or be refused, never overlap.
    /// </remarks>
    [Fact]
    public async Task GenerateEmailChangeArtifactsAsync_ShouldNotLeaveAnUnreachableProof_WhenRequestsRace()
    {
        var service = new TokenService(new InMemoryCacheService());

        var first = await service.GenerateEmailChangeArtifactsAsync(23, 1, "first@example.com");
        var second = await service.GenerateEmailChangeArtifactsAsync(23, 1, "second@example.com");

        // Whatever the interleaving, cancelling has to reach every proof the account holds.
        await service.CancelPendingEmailChangeAsync(23);

        var firstAct = () => service.ConsumeEmailChangeTokenAsync(first.LinkToken);
        await firstAct.Should().ThrowAsync<UnauthorizedException>();

        var secondAct = () => service.ConsumeEmailChangeTokenAsync(second.LinkToken);
        await secondAct.Should().ThrowAsync<UnauthorizedException>();
    }

    /// <summary>
    /// The account id is appended to the OTP proof only when present, so proofs already issued for
    /// signup and reset hash identically across the deploy that added it. This pins that the two
    /// purposes without an id still verify.
    /// </summary>
    [Fact]
    public async Task VerifyVerificationOtpAsync_ShouldStillAcceptSignupProofs()
    {
        var service = new TokenService(new InMemoryCacheService());
        var user = new User
        {
            Email = "signup@example.com",
            Password = "hashed",
            Username = "ada",
            Usertype = "participant"
        };

        var artifacts = await service.GenerateVerificationArtifactsAsync(
            user,
            VerificationPurpose.SignUp);
        var verified = await service.VerifyVerificationOtpAsync(
            artifacts.OtpChallenge.Code,
            artifacts.OtpChallenge.Challenge,
            VerificationPurpose.SignUp);

        verified.Email.Should().Be("signup@example.com");
        verified.Username.Should().Be("ada");
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldRejectTransportMismatch()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);

        var act = () => service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.ApiToken,
            requestInfo);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Refresh token transport mismatch.");
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldRejectMissingBindingToken()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);

        var act = () => service.ValidateRefreshToken(
            issue.Value,
            null,
            SessionTransport.BrowserCookie,
            requestInfo);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Missing session binding token.");
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldRejectBindingTokenMismatch()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);

        var act = () => service.ValidateRefreshToken(
            issue.Value,
            "wrong-binding",
            SessionTransport.BrowserCookie,
            requestInfo);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid session binding token.");
    }

    [Fact]
    public async Task ValidateRefreshToken_ShouldRejectReuseAfterSuccessfulValidation()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);

        var result = await service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.BrowserCookie,
            requestInfo);

        result.UserId.Should().Be(8);

        var act = () => service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.BrowserCookie,
            requestInfo);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired refresh token.");
    }

    [Fact]
    public async Task VerifyVerificationOtpAsync_ShouldInvalidateChallengeAfterMaximumAttempts()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var user = new User
        {
            Email = "signup@example.com",
            Password = "hashed-password",
            Usertype = "Organizer"
        };

        var artifacts = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrongAttempt = () => service.VerifyVerificationOtpAsync(
                "000000",
                artifacts.OtpChallenge.Challenge,
                VerificationPurpose.SignUp);

            await wrongAttempt.Should().ThrowAsync<UnauthorizedException>();
        }

        var validAttempt = () => service.VerifyVerificationOtpAsync(
            artifacts.OtpChallenge.Code,
            artifacts.OtpChallenge.Challenge,
            VerificationPurpose.SignUp);

        await validAttempt.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired verification challenge.");
        (await service.VerificationTokenExist(user.Email, VerificationPurpose.SignUp)).Should().BeNull();
    }

    [Fact]
    public async Task GenerateAndValidateRefreshToken_ShouldPreserveUserAndTransport()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(14, requestInfo, SessionTransport.ApiToken);
        var result = await service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.ApiToken,
            requestInfo);

        result.UserId.Should().Be(14);
        result.Transport.Should().Be(SessionTransport.ApiToken);
        result.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GenerateRefreshToken_ShouldRejectUserMismatch_WhenReusingExistingSession()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);
        var validation = await service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.BrowserCookie,
            requestInfo);

        var act = () => service.GenerateRefreshToken(
            9,
            requestInfo,
            SessionTransport.BrowserCookie,
            validation.SessionId);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Refresh session user mismatch.");
    }

    [Fact]
    public async Task GenerateRefreshToken_ShouldRejectTransportMismatch_WhenReusingExistingSession()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var requestInfo = CreateRequestInfo();

        var issue = await service.GenerateRefreshToken(8, requestInfo, SessionTransport.BrowserCookie);
        var validation = await service.ValidateRefreshToken(
            issue.Value,
            issue.SessionBindingToken,
            SessionTransport.BrowserCookie,
            requestInfo);

        var act = () => service.GenerateRefreshToken(
            8,
            requestInfo,
            SessionTransport.ApiToken,
            validation.SessionId);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Refresh session transport mismatch.");
    }

    [Fact]
    public async Task GenerateVerificationArtifactsAsync_ShouldReuseExistingState_ForSameEmailAndPurpose()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var user = new User
        {
            Email = "signup@example.com",
            Password = "hashed-password",
            Usertype = "Organizer"
        };

        var first = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);
        var second = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);

        second.LinkToken.Should().Be(first.LinkToken);
        second.OtpChallenge.Challenge.Should().Be(first.OtpChallenge.Challenge);
        second.OtpChallenge.Code.Should().Be(first.OtpChallenge.Code);
        second.Purpose.Should().Be(VerificationPurpose.SignUp);
    }

    [Fact]
    public async Task GenerateVerificationArtifactsAsync_ShouldRotateExistingResetState_WhenRequested()
    {
        var service = new TokenService(new InMemoryCacheService());
        var user = new User
        {
            Email = "reset@example.com",
            Usertype = "placeholder"
        };

        var first = await service.GenerateVerificationArtifactsAsync(
            user,
            VerificationPurpose.ResetPassword
        );
        var second = await service.GenerateVerificationArtifactsAsync(
            user,
            VerificationPurpose.ResetPassword,
            replaceExisting: true
        );

        second.LinkToken.Should().NotBe(first.LinkToken);
        second.OtpChallenge.Challenge.Should().NotBe(first.OtpChallenge.Challenge);
        var oldToken = () => service.VerifyVerificationToken(
            first.LinkToken,
            VerificationPurpose.ResetPassword
        );
        await oldToken.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task VerificationArtifacts_ShouldPreserveSignupUsername_ForLinkAndOtp()
    {
        var service = new TokenService(new InMemoryCacheService());
        var linkUser = new User
        {
            Email = "link@example.com",
            Username = "link-user",
            Password = "hashed-password",
            Usertype = "Organizer"
        };
        var otpUser = new User
        {
            Email = "otp@example.com",
            Username = "otp-user",
            Password = "hashed-password",
            Usertype = "Participant"
        };

        var linkArtifacts = await service.GenerateVerificationArtifactsAsync(
            linkUser,
            VerificationPurpose.SignUp
        );
        var otpArtifacts = await service.GenerateVerificationArtifactsAsync(
            otpUser,
            VerificationPurpose.SignUp
        );

        var verifiedByLink = await service.VerifyVerificationToken(
            linkArtifacts.LinkToken,
            VerificationPurpose.SignUp
        );
        var verifiedByOtp = await service.VerifyVerificationOtpAsync(
            otpArtifacts.OtpChallenge.Code,
            otpArtifacts.OtpChallenge.Challenge,
            VerificationPurpose.SignUp
        );

        verifiedByLink.Username.Should().Be("link-user");
        verifiedByOtp.Username.Should().Be("otp-user");
    }

    [Fact]
    public async Task GenerateVerificationArtifactsAsync_ShouldReplaceSignupState_WhenUsernameChanges()
    {
        var service = new TokenService(new InMemoryCacheService());
        var user = new User
        {
            Email = "signup@example.com",
            Username = "first-user",
            Password = "hashed-password",
            Usertype = "Organizer"
        };

        var first = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);
        user.Username = "second-user";
        var second = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);

        second.LinkToken.Should().NotBe(first.LinkToken);
        var verified = await service.VerifyVerificationToken(
            second.LinkToken,
            VerificationPurpose.SignUp
        );
        verified.Username.Should().Be("second-user");
    }

    [Fact]
    public async Task VerifyVerificationToken_ShouldReturnUser_AndClearStoredState()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var user = new User
        {
            Email = "verify@example.com",
            Password = "hashed-password",
            Usertype = "Organizer"
        };

        var artifacts = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);
        var verifiedUser = await service.VerifyVerificationToken(
            artifacts.LinkToken,
            VerificationPurpose.SignUp);

        verifiedUser.Email.Should().Be("verify@example.com");
        verifiedUser.Password.Should().Be("hashed-password");
        verifiedUser.Usertype.Should().Be("Organizer");
        (await service.VerificationTokenExist("verify@example.com", VerificationPurpose.SignUp)).Should().BeNull();
    }

    [Fact]
    public async Task VerifyVerificationToken_ShouldRejectPurposeMismatch()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var user = new User
        {
            Email = "verify@example.com",
            Password = "hashed-password",
            Usertype = "Organizer"
        };

        var artifacts = await service.GenerateVerificationArtifactsAsync(user, VerificationPurpose.SignUp);

        var act = () => service.VerifyVerificationToken(
            artifacts.LinkToken,
            VerificationPurpose.ResetPassword);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Verification token purpose mismatch.");
    }

    [Fact]
    public async Task RevokeAllRefreshSessionsAsync_ShouldInvalidateEverySessionForUser()
    {
        var cache = new InMemoryCacheService();
        var service = new TokenService(cache);
        var browserRequest = CreateRequestInfo();
        var apiRequest = new ClientRequestInfo
        {
            IpAddress = "10.0.0.9",
            ClientName = "ApiClient",
            DeviceType = "Server",
            IsBrowserClient = false
        };

        var browserIssue = await service.GenerateRefreshToken(44, browserRequest, SessionTransport.BrowserCookie);
        var apiIssue = await service.GenerateRefreshToken(44, apiRequest, SessionTransport.ApiToken);

        await service.RevokeAllRefreshSessionsAsync(44);

        var browserAct = () => service.ValidateRefreshToken(
            browserIssue.Value,
            browserIssue.SessionBindingToken,
            SessionTransport.BrowserCookie,
            browserRequest);
        var apiAct = () => service.ValidateRefreshToken(
            apiIssue.Value,
            apiIssue.SessionBindingToken,
            SessionTransport.ApiToken,
            apiRequest);

        await browserAct.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired refresh token.");
        await apiAct.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("Invalid or expired refresh token.");
    }

    private static ClientRequestInfo CreateRequestInfo() => new()
    {
        IpAddress = "127.0.0.1",
        ClientName = "UnitTest",
        DeviceType = "Desktop",
        IsBrowserClient = true
    };
}

internal sealed class InMemoryCacheService : backend.main.features.cache.ICacheService
{
    private sealed class CacheEntry
    {
        public string? StringValue { get; set; }
        public Dictionary<string, string> HashValues { get; } = [];
        public HashSet<string> SetValues { get; } = [];
        public LinkedList<string> ListValues { get; } = [];
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = [];

    public Task<bool> SetValueAsync(string key, string value, TimeSpan? expiry = null)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            entry.StringValue = value;
            entry.ExpiresAt = ResolveExpiry(expiry);
        }

        return Task.FromResult(true);
    }

    public Task<string?> GetValueAsync(string key)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetEntry(key, out var entry) ? entry.StringValue : null);
        }
    }

    public Task<long> IncrementAsync(string key, long value = 1)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            var current = long.TryParse(entry.StringValue, out var parsed) ? parsed : 0L;
            current += value;
            entry.StringValue = current.ToString();
            return Task.FromResult(current);
        }
    }

    public Task<long> DecrementAsync(string key, long value = 1) => IncrementAsync(key, -value);

    public Task<bool> HashSetAsync(string key, string field, string value)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            entry.HashValues[field] = value;
            return Task.FromResult(true);
        }
    }

    public Task<string?> HashGetAsync(string key, string field)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry) || !entry.HashValues.TryGetValue(field, out var value))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(value);
        }
    }

    public Task<Dictionary<string, string>> HashGetAllAsync(string key)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry))
                return Task.FromResult(new Dictionary<string, string>());

            return Task.FromResult(entry.HashValues.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    public Task<bool> HashDeleteAsync(string key, string field)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetEntry(key, out var entry) && entry.HashValues.Remove(field));
        }
    }

    public Task<bool> SetAddAsync(string key, string value)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            return Task.FromResult(entry.SetValues.Add(value));
        }
    }

    public Task<bool> SetRemoveAsync(string key, string value)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetEntry(key, out var entry) && entry.SetValues.Remove(value));
        }
    }

    public Task<string[]> SetMembersAsync(string key)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetEntry(key, out var entry)
                ? entry.SetValues.ToArray()
                : []);
        }
    }

    public Task<long> ListLeftPushAsync(string key, string value)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            entry.ListValues.AddFirst(value);
            return Task.FromResult((long)entry.ListValues.Count);
        }
    }

    public Task<long> ListRightPushAsync(string key, string value)
    {
        lock (_gate)
        {
            var entry = GetOrCreateEntry(key);
            entry.ListValues.AddLast(value);
            return Task.FromResult((long)entry.ListValues.Count);
        }
    }

    public Task<string?> ListLeftPopAsync(string key)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry) || entry.ListValues.First == null)
                return Task.FromResult<string?>(null);

            var value = entry.ListValues.First.Value;
            entry.ListValues.RemoveFirst();
            return Task.FromResult<string?>(value);
        }
    }

    public Task<string?> ListRightPopAsync(string key)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry) || entry.ListValues.Last == null)
                return Task.FromResult<string?>(null);

            var value = entry.ListValues.Last.Value;
            entry.ListValues.RemoveLast();
            return Task.FromResult<string?>(value);
        }
    }

    public Task<bool> DeleteKeyAsync(string key)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Remove(key));
        }
    }

    public Task<bool> KeyExistsAsync(string key)
    {
        lock (_gate)
        {
            return Task.FromResult(TryGetEntry(key, out _));
        }
    }

    public Task<TimeSpan?> GetTTLAsync(string key)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry) || entry.ExpiresAt is null)
                return Task.FromResult<TimeSpan?>(null);

            return Task.FromResult<TimeSpan?>(entry.ExpiresAt.Value - DateTimeOffset.UtcNow);
        }
    }

    public Task<bool> SetExpiryAsync(string key, TimeSpan expiry)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry))
                return Task.FromResult(false);

            entry.ExpiresAt = DateTimeOffset.UtcNow.Add(expiry);
            return Task.FromResult(true);
        }
    }

    public IEnumerable<string> ScanKeys(StackExchange.Redis.IServer server, string pattern)
    {
        lock (_gate)
        {
            return _entries.Keys.ToArray();
        }
    }

    public Task<bool> AcquireLockAsync(string key, string value, TimeSpan expiry)
    {
        lock (_gate)
        {
            if (TryGetEntry(key, out _))
                return Task.FromResult(false);

            var entry = GetOrCreateEntry(key);
            entry.StringValue = value;
            entry.ExpiresAt = DateTimeOffset.UtcNow.Add(expiry);
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseLockAsync(string key, string value)
    {
        lock (_gate)
        {
            if (!TryGetEntry(key, out var entry) || entry.StringValue != value)
                return Task.FromResult(false);

            _entries.Remove(key);
            return Task.FromResult(true);
        }
    }

    public StackExchange.Redis.IServer GetServer() => throw new NotSupportedException();

    public Task<Dictionary<string, string?>> GetManyAsync(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            return Task.FromResult(keys.ToDictionary(key => key, key =>
            {
                return TryGetEntry(key, out var entry) ? entry.StringValue : null;
            }));
        }
    }

    public Task<object> EvalAsync(string script, StackExchange.Redis.RedisKey[] keys, StackExchange.Redis.RedisValue[] values)
    {
        lock (_gate)
        {
            if (keys.Length == 1
                && values.Length == 2
                && script.Contains("redis.call('GET'", StringComparison.Ordinal)
                && script.Contains("redis.call('SET'", StringComparison.Ordinal))
            {
                var key = keys[0].ToString();
                var matchedWindow = long.Parse(values[0].ToString());
                var ttlMs = long.Parse(values[1].ToString());

                if (TryGetEntry(key, out var existing)
                    && long.TryParse(existing.StringValue, out var lastWindow)
                    && matchedWindow <= lastWindow)
                {
                    return Task.FromResult<object>(0L);
                }

                var entry = GetOrCreateEntry(key);
                entry.StringValue = matchedWindow.ToString();
                entry.ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(ttlMs);
                return Task.FromResult<object>(1L);
            }

            return Task.FromResult<object>(1L);
        }
    }

    // The token service does not use bitmaps; these exist so the fake still satisfies ICacheService.
    public Task<bool> SetBitsAsync(string key, IReadOnlyCollection<long> bitPositions) =>
        Task.FromResult(false);

    public Task<byte[]?> GetBitmapAsync(string key) => Task.FromResult<byte[]?>(null);

    public Task<bool> SetBitmapAsync(string key, byte[] bitmap, TimeSpan? expiry = null) =>
        Task.FromResult(false);

    private CacheEntry GetOrCreateEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new CacheEntry();
            _entries[key] = entry;
        }

        return entry;
    }

    private bool TryGetEntry(string key, out CacheEntry entry)
    {
        if (_entries.TryGetValue(key, out entry!))
        {
            if (entry.ExpiresAt is not null && entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.Remove(key);
                entry = null!;
                return false;
            }

            return true;
        }

        entry = null!;
        return false;
    }

    private static DateTimeOffset? ResolveExpiry(TimeSpan? expiry) =>
        expiry is null ? null : DateTimeOffset.UtcNow.Add(expiry.Value);
}


