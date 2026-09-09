using System.Net;
using System.Text.Json;

using backend.main.features.profile.contracts.responses;

using backend.tests.Integration.Infrastructure;

using FluentAssertions;

namespace backend.tests.Integration.Features.Profile;

/// <summary>
/// Covers the username display form against a real Postgres.
/// </summary>
/// <remarks>
/// These have to run here rather than as unit tests: <c>Users.Username</c> is <c>citext</c>, and the
/// in-memory SQLite harness has no such type. Case-insensitive uniqueness and case-insensitive
/// lookup — the two properties that make it safe to render a mixed-case handle beside a lowercase
/// key — only actually hold on Postgres.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class UsernameDisplayEndpointTests
{
    [Fact]
    public async Task SignUp_ShouldKeepTheCasingTheUserTypedAndLowercaseTheKey()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await app.SignUpAndVerifyByTokenAsync(
            "display-user@example.com",
            username: "ThomasT");

        var response = await app.GetWithBearerAsync("/api/profile", session.AccessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var profile = await app.ReadApiResponseAsync<MyProfileResponse>(response);
        profile.Data!.Username.Should().Be("thomast");
        profile.Data.UsernameDisplay.Should().Be("ThomasT");
    }

    /// <summary>
    /// The rule that keeps the column safe to store: the display form differs from the key by
    /// letter case and nothing else.
    /// </summary>
    [Fact]
    public async Task SignUp_ShouldStoreADisplayThatLowercasesToTheUsername()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await app.SignUpAndVerifyByTokenAsync(
            "display-invariant@example.com",
            username: "MixedCaseName");

        var response = await app.GetWithBearerAsync("/api/profile", session.AccessToken);
        var profile = await app.ReadApiResponseAsync<MyProfileResponse>(response);

        profile.Data!.UsernameDisplay.ToLowerInvariant().Should().Be(profile.Data.Username);
    }

    /// <summary>
    /// The regression that would follow from letting a read path resolve by the display form: the
    /// public profile must still be reachable by the lowercase key, and by any casing of it, since
    /// citext decides the match.
    /// </summary>
    [Theory]
    [InlineData("thomast")]
    [InlineData("ThomasT")]
    [InlineData("THOMAST")]
    public async Task PublicProfile_ShouldResolveByAnyCasingAndRenderTheDisplayForm(string lookup)
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SignUpAndVerifyByTokenAsync("display-public@example.com", username: "ThomasT");

        var response = await app.Client.GetAsync($"/api/profile/{lookup}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await app.ReadApiResponseAsync<PublicProfileResponse>(response);
        profile.Data!.Username.Should().Be("thomast");
        profile.Data.UsernameDisplay.Should().Be("ThomasT");
    }

    /// <summary>
    /// Uniqueness is still owned by the citext unique index, so a differently-cased duplicate is a
    /// conflict rather than a second account.
    /// </summary>
    [Fact]
    public async Task SignUp_ShouldRejectANameThatDiffersOnlyByCase()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SignUpAndVerifyByTokenAsync("display-first@example.com", username: "ThomasT");

        var act = () => app.SignUpAndVerifyByTokenAsync(
            "display-second@example.com",
            username: "THOMAST");

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// Login resolves through <c>UsernamePolicy.Normalize</c>, which must stay display-blind.
    /// </summary>
    [Fact]
    public async Task Login_ShouldAcceptAnyCasingOfTheUsername()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SignUpAndVerifyByTokenAsync("display-login@example.com", username: "ThomasT");

        // Signing up does not register a trusted device, and an unknown one is met with a step-up
        // challenge rather than a session — so seed one, as every other login test here does.
        var user = await app.FindUserByEmailAsync("display-login@example.com");
        user.Should().NotBeNull();
        await app.SeedKnownDeviceAsync(user!.Id, "display-login-device");

        // LoginApiAsync asserts a 200 and an authenticated payload internally, so reaching a
        // session at all is the assertion here.
        var session = await app.LoginApiAsync("THOMAST", trustedDeviceToken: "display-login-device");

        session.AccessToken.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Pins the wire contract the Angular client reads. Deserialising into the C# response type is
    /// case-insensitive, so only inspecting the raw JSON can catch a casing change.
    /// </summary>
    [Fact]
    public async Task Profile_ShouldSerializeCamelCasePropertyNames()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await app.SignUpAndVerifyByTokenAsync(
            "display-wire@example.com",
            username: "ThomasT");

        var response = await app.GetWithBearerAsync("/api/profile", session.AccessToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var data = document.RootElement.GetProperty("data");
        data.GetProperty("username").GetString().Should().Be("thomast");
        data.GetProperty("usernameDisplay").GetString().Should().Be("ThomasT");
    }
}
