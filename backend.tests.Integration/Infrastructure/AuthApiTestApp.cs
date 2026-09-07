using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using backend.main.application.security;
using backend.main.features.auth.contracts.requests;
using backend.main.features.auth.contracts.responses;
using backend.main.features.auth.device;
using backend.main.features.auth.mfa;
using backend.main.features.auth.oauth;
using backend.main.features.auth.token;
using backend.main.features.bloom;
using backend.main.features.cache;
using backend.main.features.clubs.posts.search;
using backend.main.features.clubs.search;
using backend.main.features.clubs.staff;
using backend.main.features.events.invitations;
using backend.main.features.events.registration;
using backend.main.features.events.search;
using backend.main.features.payment;
using backend.main.features.profile;
using backend.main.infrastructure.database.core;
using backend.main.shared.providers.messages;
using backend.main.utilities;

using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace backend.tests.Integration.Infrastructure;

public sealed class AuthApiTestApp : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TestWebApplicationFactory _factory;
    private readonly AuthApiTestAppPool.Lease _lease;
    private readonly KafkaTopicProbe _kafkaProbe;
    private readonly TestResourceNamespace _resources;

    public HttpClient Client { get; }
    public ICacheService Cache => _factory.Services.GetRequiredService<ICacheService>();
    /// <summary>The running app's filter registry, for asserting what the advisory endpoints can rely on.</summary>
    public IBloomFilterRegistry BloomFilters =>
        _factory.Services.GetRequiredService<IBloomFilterRegistry>();
    /// <summary>Routing metadata for the running app, for asserting endpoint configuration.</summary>
    public EndpointDataSource Endpoints =>
        _factory.Services.GetRequiredService<EndpointDataSource>();
    public KafkaBackedPublisher Publisher { get; }
    public FakeCaptchaService Captcha => _factory.Captcha;
    public FakeOAuthService OAuth => _factory.OAuth;
    public FakeAzureBlobService BlobStorage => _factory.BlobStorage;
    internal int ResourceSlot => _resources.Slot;

    private AuthApiTestApp(
        TestWebApplicationFactory factory,
        HttpClient client,
        AuthApiTestAppPool.Lease lease,
        KafkaTopicProbe kafkaProbe,
        TestResourceNamespace resources)
    {
        _factory = factory;
        Client = client;
        _lease = lease;
        _kafkaProbe = kafkaProbe;
        _resources = resources;
        Publisher = new KafkaBackedPublisher(this);
    }

    public static async Task<AuthApiTestApp> CreateAsync(
        Action<IServiceCollection>? serviceOverrides = null,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        var lease = await AuthApiTestAppPool.AcquireAsync(
            serviceOverrides,
            configurationOverrides);
        try
        {
            var client = lease.Factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36");

            var app = new AuthApiTestApp(
                lease.Factory,
                client,
                lease,
                lease.Environment.CreateKafkaProbe(),
                lease.Resources);
            await app.MarkNotificationBoundaryAsync();
            return app;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public async Task<User> SeedUserAsync(
        string email,
        string password = "Password123!",
        string role = "Participant",
        bool disabled = false,
        string? googleId = null,
        string? microsoftId = null,
        string? username = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();

        var user = new User
        {
            Email = email,
            Username = username ?? email.Split('@')[0],
            Password = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4),
            Usertype = role,
            IsDisabled = disabled,
            GoogleID = googleId,
            MicrosoftID = microsoftId
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task SeedKnownDeviceAsync(
        int userId,
        string trustedDeviceToken,
        string deviceType = "Desktop",
        string clientName = "Chrome")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        db.Devices.Add(new Device
        {
            UserId = userId,
            DeviceTokenHash = ComputeHash(trustedDeviceToken),
            DeviceType = deviceType,
            ClientName = clientName,
            IpAddress = "127.0.0.1"
        });
        await db.SaveChangesAsync();
    }

    public async Task<User?> FindUserByEmailAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        return await db.Users.SingleOrDefaultAsync(user => user.Email == email);
    }

    public async Task<T> QueryDbAsync<T>(Func<AppDatabaseContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        return await query(db);
    }

    public async Task<string> DescribeFailureAsync(HttpResponseMessage response, int maxLogLines = 80)
    {
        var body = await response.Content.ReadAsStringAsync();
        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logsDirectory))
            return $"response body:{Environment.NewLine}{body}";

        var latestLogPath = Directory
            .GetFiles(logsDirectory, "*", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latestLogPath is null)
            return $"response body:{Environment.NewLine}{body}";

        var logTail = File
            .ReadLines(latestLogPath)
            .TakeLast(maxLogLines);

        return string.Join(
            Environment.NewLine,
            [
                $"response body:{Environment.NewLine}{body}",
                $"latest log: {latestLogPath}",
                "log tail:",
                string.Join(Environment.NewLine, logTail)
            ]);
    }

    public async Task<SmsMfaEnrollment?> FindSmsMfaEnrollmentAsync(int userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        return await db.SmsMfaEnrollments.SingleOrDefaultAsync(enrollment => enrollment.UserId == userId);
    }

    /// <summary>
    /// Builds a SignalR client bound to the in-memory test server.
    /// </summary>
    /// <remarks>
    /// The token is passed as an <c>access_token</c> query parameter rather than an
    /// Authorization header on purpose: that is what a browser does on a WebSocket
    /// handshake, so this exercises the query-string branch of the JWT bearer events.
    /// </remarks>
    public HubConnection CreateHubConnection(string hubPath, string? accessToken = null)
    {
        var server = _factory.Server;
        var url = string.IsNullOrEmpty(accessToken)
            ? hubPath
            : $"{hubPath}?access_token={Uri.EscapeDataString(accessToken)}";

        return new HubConnectionBuilder()
            .WithUrl(new Uri(Client.BaseAddress!, url), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socketClient = server.CreateWebSocketClient();
                    var socketUri = new UriBuilder(context.Uri) { Scheme = "http" }.Uri;
                    return await socketClient.ConnectAsync(socketUri, cancellationToken);
                };
            })
            .Build();
    }

    public async Task<HttpResponseMessage> GetWithBearerAsync(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostJsonWithBearerAndCsrfAsync(
        string path,
        object payload,
        string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add(CsrfConfiguration.CsrfHeaderName, await GetCsrfTokenAsync());
        return await Client.SendAsync(request);
    }

    /// <summary>
    /// Satisfies a <c>[RequireMfa]</c> gate for the given session by completing an
    /// in-session email step-up (start + verify with the emitted code).
    /// </summary>
    public async Task CompleteSessionMfaByEmailAsync(string email, string accessToken)
    {
        var start = await PostJsonWithBearerAndCsrfAsync(
            "/api/auth/mfa/step-up/start",
            new SessionMfaStartRequest { Method = "email" },
            accessToken);
        start.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var codeEmail = await WaitForEmailAsync(
            message => message.Type == EmailMessageType.MfaCode && message.Email == email);

        var verify = await PostJsonWithBearerAndCsrfAsync(
            "/api/auth/mfa/step-up/verify",
            new SessionMfaVerifyRequest { Method = "email", Code = codeEmail.Code! },
            accessToken);
        verify.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    public async Task AddClubStaffAsync(int clubId, int userId, int grantedByUserId, ClubStaffRole role = ClubStaffRole.Manager)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();

        db.ClubStaff.Add(new ClubStaff
        {
            ClubId = clubId,
            UserId = userId,
            GrantedByUserId = grantedByUserId,
            Role = role
        });

        await db.SaveChangesAsync();
    }

    public async Task AddAcceptedInvitationAsync(int eventId, int userId, string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();

        db.EventInvitations.Add(new EventInvitation
        {
            EventId = eventId,
            RecipientUserId = userId,
            RecipientEmail = email,
            RecipientEmailNormalized = email.Trim().ToLowerInvariant(),
            SourceType = EventInvitationSource.DirectUser,
            LifecycleStatus = EventInvitationLifecycleStatus.Accepted,
            DeliveryStatus = EventInvitationDeliveryStatus.Sent,
            AcceptedAtUtc = DateTime.UtcNow,
            AcceptedByUserId = userId
        });

        await db.SaveChangesAsync();
    }

    public async Task AddRegistrationAsync(int eventId, int userId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();

        db.EventRegistrations.Add(new EventRegistration
        {
            EventId = eventId,
            UserId = userId,
            Status = RegistrationStatus.Active
        });

        var ev = await db.Events.FirstAsync(existing => existing.Id == eventId);
        ev.RegistrationCount += 1;
        ev.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        await ReindexEventsAsync();
    }

    public async Task SetEventStartTimeToPast(int eventId, int minutesAgo = 10)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        var ev = await db.Events.FirstAsync(e => e.Id == eventId);
        ev.StartTime = DateTime.UtcNow.AddMinutes(-minutesAgo);
        ev.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await ReindexEventsAsync();
    }

    public async Task SetEventEndTimeToPast(int eventId, int minutesAgo = 5)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        var ev = await db.Events.FirstAsync(e => e.Id == eventId);
        ev.StartTime = DateTime.UtcNow.AddHours(-2);
        ev.EndTime = DateTime.UtcNow.AddMinutes(-minutesAgo);
        ev.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await ReindexEventsAsync();
    }

    public async Task SetMaxParticipants(int eventId, int max)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();
        var ev = await db.Events.FirstAsync(e => e.Id == eventId);
        ev.maxParticipants = max;
        ev.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await ReindexEventsAsync();
    }

    public async Task AddPaymentAsync(int eventId, int userId, PaymentStatus status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDatabaseContext>();

        db.Payments.Add(new Payment
        {
            EventId = eventId,
            UserId = userId,
            Amount = 1000,
            Currency = "usd",
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }

    public async Task DeletePendingOAuthSignupAsync(string signupToken)
    {
        await Cache.DeleteKeyAsync($"oauth:pending:{signupToken}");
    }

    public Task MarkNotificationBoundaryAsync() =>
        _kafkaProbe.MarkBoundaryAsync(
            _resources.EmailTopic,
            _resources.SmsTopic,
            _resources.EmailStatusTopic);

    public Task<EmailMessage> WaitForEmailAsync(
        Func<EmailMessage, bool> predicate,
        TimeSpan? timeout = null) =>
        _kafkaProbe.WaitForAsync(_resources.EmailTopic, predicate, timeout);

    public Task<SmsMfaMessage> WaitForSmsAsync(
        Func<SmsMfaMessage, bool> predicate,
        TimeSpan? timeout = null) =>
        _kafkaProbe.WaitForAsync(_resources.SmsTopic, predicate, timeout);

    public Task<IReadOnlyList<EmailMessage>> ReadNewEmailMessagesAsync(TimeSpan? timeout = null) =>
        _kafkaProbe.ReadNewAsync<EmailMessage>(_resources.EmailTopic, timeout);

    public Task<IReadOnlyList<SmsMfaMessage>> ReadNewSmsMessagesAsync(TimeSpan? timeout = null) =>
        _kafkaProbe.ReadNewAsync<SmsMfaMessage>(_resources.SmsTopic, timeout);

    public async Task<int> ReindexEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var reindexService = scope.ServiceProvider.GetRequiredService<IEventReindexService>();
        return await reindexService.ReindexAllAsync(cancellationToken);
    }

    public async Task ReindexClubsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var reindexService = scope.ServiceProvider.GetRequiredService<IClubReindexService>();
        await reindexService.ReindexAllAsync(cancellationToken);
    }

    public async Task ReindexClubPostsAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var reindexService = scope.ServiceProvider.GetRequiredService<IClubPostReindexService>();
        await reindexService.ReindexAllAsync(cancellationToken);
    }

    public async Task<HttpResponseMessage> PostJsonWithCsrfAsync(string path, object payload)
    {
        var token = await GetCsrfTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add(CsrfConfiguration.CsrfHeaderName, token);
        return await Client.SendAsync(request);
    }

    public async Task<string> GetCsrfTokenAsync()
    {
        var response = await Client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ApiEnvelope<CsrfTokenPayload>>(JsonOptions);
        return body!.Data!.Token;
    }

    public async Task<ApiEnvelope<T>> ReadApiResponseAsync<T>(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        payload.Should().NotBeNull();
        return payload!;
    }

    public async Task<AuthenticatedSessionResponse> SignUpAndVerifyByTokenAsync(
        string email,
        string password = "Password123!",
        string role = "Participant",
        string? transport = null,
        string? username = null)
    {
        username ??= email.Split('@')[0];
        var signupResponse = await PostJsonWithCsrfAsync("/api/auth/signup", new SignUpRequest
        {
            Email = email,
            Username = username,
            Password = password,
            Usertype = role,
            Captcha = "captcha"
        });
        signupResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var verifyEmail = await WaitForEmailAsync(
            message => message.Type == EmailMessageType.VerifyEmail && message.Email == email);

        var verifyResponse = await PostJsonWithCsrfAsync("/api/auth/verify", new VerificationTokenRequest
        {
            Token = verifyEmail.Token,
            Transport = transport
        });
        verifyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var apiResponse = await ReadApiResponseAsync<AuthenticatedSessionResponse>(verifyResponse);
        apiResponse.Data.Should().NotBeNull();
        return apiResponse.Data!;
    }

    public async Task<AuthenticatedSessionResponse> LoginApiAsync(
        string username,
        string password = "Password123!",
        string trustedDeviceToken = "known-device")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Username = username,
                Password = password,
                Captcha = "captcha",
                Transport = SessionTransportResolver.ApiValue
            })
        };
        request.Headers.Add(HttpUtility.TrustedDeviceHeaderName, trustedDeviceToken);
        request.Headers.Add(CsrfConfiguration.CsrfHeaderName, await GetCsrfTokenAsync());

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var apiResponse = await ReadApiResponseAsync<LoginAuthenticationResponse>(response);
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Type.Should().Be("authenticated");
        apiResponse.Data.Auth.Should().NotBeNull();
        return apiResponse.Data.Auth!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _lease.DisposeAsync();
    }

    public static string? ExtractCookie(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return null;

        foreach (var value in values)
        {
            if (!value.StartsWith($"{cookieName}=", StringComparison.OrdinalIgnoreCase))
                continue;

            var start = cookieName.Length + 1;
            var end = value.IndexOf(';');
            return end >= 0 ? value[start..end] : value[start..];
        }

        return null;
    }

    private static string ComputeHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    private sealed class CsrfTokenPayload
    {
        public required string Token { get; init; }
    }

    public sealed class ApiEnvelope<T>
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public T? Data { get; init; }
    }
}





