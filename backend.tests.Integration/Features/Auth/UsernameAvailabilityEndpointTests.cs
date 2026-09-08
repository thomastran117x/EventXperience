using System.Net;

using backend.main.features.auth.contracts.responses;

using backend.tests.Integration.Infrastructure;

using FluentAssertions;

namespace backend.tests.Integration.Features.Auth;

/// <summary>
/// Exercises the availability endpoint against a real Postgres and Redis.
/// </summary>
/// <remarks>
/// Covers the endpoint and its database fallback, not the filter itself: nothing hydrates the
/// registry in this host, so every lookup reports Unavailable and falls through to the
/// repository. See the remarks on <c>EmailAvailabilityEndpointTests</c>, which pins that.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class UsernameAvailabilityEndpointTests
{
    [Fact]
    public async Task CheckAvailability_ShouldReportAnUnusedNameAsAvailable()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/availability?username=never-registered");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(response);
        body.Data!.Available.Should().BeTrue();
        body.Data.Username.Should().Be("never-registered");
    }

    /// <summary>
    /// Seeded users are written straight through the DbContext and never touch the filter, so a
    /// correct answer here proves the endpoint still confirms against the database.
    /// </summary>
    [Fact]
    public async Task CheckAvailability_ShouldReportASeededNameAsTaken()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SeedUserAsync("seeded-availability@example.com", username: "seeded-availability");

        var response = await app.Client.GetAsync("/api/auth/username/availability?username=seeded-availability");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(response);
        body.Data!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAvailability_ShouldReflectAUsernameClaimedThroughSignup()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var before = await app.Client.GetAsync("/api/auth/username/availability?username=claimed-by-signup");
        (await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(before)).Data!.Available.Should().BeTrue();

        await app.SignUpAndVerifyByTokenAsync("claimed@example.com", username: "claimed-by-signup");

        var after = await app.Client.GetAsync("/api/auth/username/availability?username=claimed-by-signup");
        (await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(after)).Data!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAvailability_ShouldNormaliseBeforeAnswering()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SeedUserAsync("mixed-case@example.com", username: "mixedcase");

        var response = await app.Client.GetAsync("/api/auth/username/availability?username=%20MixedCase%20");

        var body = await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(response);
        body.Data!.Username.Should().Be("mixedcase");
        body.Data.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ab")]
    [InlineData("a..b")]
    [InlineData(".ab")]
    [InlineData("ab-")]
    [InlineData("a%20b")]
    [InlineData("admin")]
    public async Task CheckAvailability_ShouldRejectNamesThePolicyDisallows(string username)
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync($"/api/auth/username/availability?username={username}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CheckAvailability_ShouldNotRequireAuthentication()
    {
        // It serves the signup form, where there is no session yet.
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/availability?username=anonymous-probe");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
