using System.Net;
using System.Text.Json;

using backend.main.features.auth.contracts.responses;
using backend.main.features.profile;

using backend.tests.Integration.Infrastructure;

using FluentAssertions;

namespace backend.tests.Integration.Features.Auth;

/// <summary>
/// Exercises the suggestion endpoint against a real Postgres and Redis.
/// </summary>
/// <remarks>
/// Nothing hydrates the bloom registry in this host, so every candidate falls through to the
/// database — which is the expensive path the batched lookup exists to bound, and therefore the
/// one worth covering here.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class UsernameSuggestionsEndpointTests
{
    [Fact]
    public async Task SuggestUsernames_ShouldReturnThreeClaimableNames()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<UsernameSuggestionsResponse>(response);
        body.Data!.Suggestions.Should().HaveCount(3);
        body.Data.Suggestions.Select(suggestion => suggestion.Username).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The endpoint serves the signup form, where there is no session to authenticate with.
    /// </summary>
    [Fact]
    public async Task SuggestUsernames_ShouldNotRequireASession()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/suggestions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The end-to-end promise: a name offered here is one the user can actually take. Advisory, so
    /// it could in principle be claimed in between — but nothing else is writing in this test.
    /// </summary>
    [Fact]
    public async Task SuggestUsernames_ShouldOnlyOfferNamesTheAvailabilityProbeAccepts()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/suggestions");
        var body = await app.ReadApiResponseAsync<UsernameSuggestionsResponse>(response);

        foreach (var suggestion in body.Data!.Suggestions)
        {
            var probe = await app.Client.GetAsync(
                $"/api/auth/username/availability?username={suggestion.Username}");
            probe.StatusCode.Should().Be(HttpStatusCode.OK);

            var availability = await app.ReadApiResponseAsync<UsernameAvailabilityResponse>(probe);
            availability.Data!.Available.Should().BeTrue($"'{suggestion.Username}' was suggested");
        }
    }

    /// <summary>
    /// A suggestion has to survive the write path it was generated for, or the chip hands the user
    /// a 400 the moment they submit it.
    /// </summary>
    [Fact]
    public async Task SuggestUsernames_ShouldReturnNamesTheWritePathAccepts()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/suggestions");
        var body = await app.ReadApiResponseAsync<UsernameSuggestionsResponse>(response);

        foreach (var suggestion in body.Data!.Suggestions)
        {
            var forms = UsernamePolicy.NormalizeAndValidateWithDisplay(suggestion.Display);
            forms.Username.Should().Be(suggestion.Username);
            forms.Display.Should().Be(suggestion.Display);
        }
    }

    /// <summary>
    /// Pins the wire contract the Angular client is typed against. The payload is camelCase, so a
    /// serializer change that silently switched casing would break the chips without failing a
    /// C# test — deserialising into the response type would keep working, since that is
    /// case-insensitive.
    /// </summary>
    [Fact]
    public async Task SuggestUsernames_ShouldSerializeCamelCasePropertyNames()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.Client.GetAsync("/api/auth/username/suggestions");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var first = document.RootElement.GetProperty("data").GetProperty("suggestions")[0];
        first.TryGetProperty("username", out _).Should().BeTrue();
        first.TryGetProperty("display", out _).Should().BeTrue();
    }
}
