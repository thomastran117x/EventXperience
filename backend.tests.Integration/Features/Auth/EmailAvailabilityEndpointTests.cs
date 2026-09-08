using System.Net;

using backend.main.features.auth.contracts.responses;
using backend.main.features.bloom;

using backend.tests.Integration.Infrastructure;

using FluentAssertions;

namespace backend.tests.Integration.Features.Auth;

/// <summary>
/// Exercises the email availability endpoint against a real Postgres and Redis.
/// </summary>
/// <remarks>
/// These cover the endpoint and its database fallback, not the filter itself. Nothing hydrates
/// the registry in this host: the only code that marks a filter ready is
/// <c>BloomFilterMaintenanceService</c>, and the Testing environment sets
/// <c>includeHostedServices: false</c>, so every lookup reports Unavailable and falls through to
/// the repository. <c>Availability_ShouldRunAgainstAnUnhydratedFilter</c> pins that, because the
/// seeded assertions below quietly depend on it. The filter logic itself is covered by the unit
/// tests around <c>BloomFilterRegistry</c>, <c>EmailBloomFilterSource</c> and
/// <c>EmailAvailabilityService</c>.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class EmailAvailabilityEndpointTests
{
    [Fact]
    public async Task CheckAvailability_ShouldReportAnUnusedAddressAsAvailable()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync(
            "/api/auth/email/availability?email=never-registered@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<EmailAvailabilityResponse>(response);
        body.Data!.Available.Should().BeTrue();
        body.Data.Email.Should().Be("never-registered@example.com");
    }

    /// <summary>
    /// Seeded users are written straight through the DbContext and never touch the filter, so a
    /// correct answer here proves the endpoint still confirms against the database.
    /// </summary>
    /// <remarks>
    /// Only sound while the filter cannot answer. If this host ever hydrates the registry, the
    /// seeded address would be provably absent from the bitmap, the advisory endpoint would skip
    /// the query, and this would report it as available. See the guard test below.
    /// </remarks>
    [Fact]
    public async Task CheckAvailability_ShouldReportASeededAddressAsRegistered()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SeedUserAsync("seeded-email@example.com", username: "seeded-email-user");

        var response = await app.Client.GetAsync(
            "/api/auth/email/availability?email=seeded-email@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<EmailAvailabilityResponse>(response);
        body.Data!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAvailability_ShouldReflectAnAddressClaimedThroughSignup()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var before = await app.Client.GetAsync(
            "/api/auth/email/availability?email=claimed-email@example.com");
        (await app.ReadApiResponseAsync<EmailAvailabilityResponse>(before)).Data!.Available
            .Should().BeTrue();

        await app.SignUpAndVerifyByTokenAsync("claimed-email@example.com", username: "claimed-email-user");

        var after = await app.Client.GetAsync(
            "/api/auth/email/availability?email=claimed-email@example.com");
        (await app.ReadApiResponseAsync<EmailAvailabilityResponse>(after)).Data!.Available
            .Should().BeFalse();
    }

    /// <summary>
    /// The column is citext so the database would match either way, but the filter hashes the
    /// literal string — this is what proves the probe and the source agree on one spelling.
    /// </summary>
    [Fact]
    public async Task CheckAvailability_ShouldNormaliseBeforeAnswering()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SeedUserAsync("mixed-case-email@example.com", username: "mixed-case-email-user");

        var response = await app.Client.GetAsync(
            "/api/auth/email/availability?email=%20Mixed-Case-Email%40Example.COM%20");

        var body = await app.ReadApiResponseAsync<EmailAvailabilityResponse>(response);
        body.Data!.Email.Should().Be("mixed-case-email@example.com");
        body.Data.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    public async Task CheckAvailability_ShouldRejectAddressesThePolicyDisallows(string email)
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync($"/api/auth/email/availability?email={email}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CheckAvailability_ShouldRejectAnOverLengthAddress()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var tooLong = new string('a', 250) + "@example.com";

        var response = await app.Client.GetAsync($"/api/auth/email/availability?email={tooLong}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CheckAvailability_ShouldNotRequireAuthentication()
    {
        // It serves the signup form, where there is no session yet.
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync(
            "/api/auth/email/availability?email=anonymous-probe@example.com");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Pins the assumption every seeded assertion here rests on.
    /// </summary>
    /// <remarks>
    /// Registering hosted services in the test host would hydrate the registry and silently
    /// invert the seeded tests, which write through the DbContext and so never reach the filter.
    /// Failing here first says why, instead of leaving someone to debug an endpoint that looks
    /// like it has started lying.
    /// </remarks>
    [Fact]
    public async Task Availability_ShouldRunAgainstAnUnhydratedFilter()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        app.BloomFilters.IsReady(BloomFilterTargets.Email).Should().BeFalse();
        app.BloomFilters.MightContain(BloomFilterTargets.Email, "never-registered@example.com")
            .Should().Be(BloomFilterLookup.Unavailable);
    }

    /// <summary>
    /// The two namespaces must not bleed into each other: registering an address must not make
    /// its local part look taken as a username, or vice versa.
    /// </summary>
    /// <remarks>
    /// End-to-end over the endpoints and their repository queries. The hash-level separation that
    /// keeps the two targets independent once the filters are live is covered by
    /// <c>BloomHashTests</c> and <c>EmailAvailabilityServiceTests</c>.
    /// </remarks>
    [Fact]
    public async Task CheckAvailability_ShouldNotLeakBetweenTheUsernameAndEmailFilters()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        await app.SignUpAndVerifyByTokenAsync("separation@example.com", username: "separation-user");

        var email = await app.Client.GetAsync(
            "/api/auth/email/availability?email=separation-user@example.com");
        (await app.ReadApiResponseAsync<EmailAvailabilityResponse>(email)).Data!.Available
            .Should().BeTrue();

        // The mirror image cannot be probed directly any more: an address is never a legal
        // username, so the endpoint answers 400 rather than consulting either filter. Probe the
        // local part instead - it is registered as an email, and must still read as a free username.
        var username = await app.Client.GetAsync(
            "/api/auth/username/availability?username=separation");
        (await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(username)).Data!.Available
            .Should().BeTrue();
    }
}
