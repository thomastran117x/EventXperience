using System.Net;

using backend.main.application.security;
using backend.main.features.auth.contracts.requests;
using backend.main.features.auth.contracts.responses;
using backend.main.features.profile;
using backend.main.features.profile.contracts.requests;
using backend.main.features.profile.contracts.responses;
using backend.main.shared.providers.messages;

using backend.tests.Integration.Infrastructure;

using FluentAssertions;

namespace backend.tests.Integration.Features.Profile;

public class EmailChangeEndpointsTests
{
    [Fact]
    public async Task RequestAndConfirm_ShouldMoveTheAccountToTheNewAddress()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "change-me@example.com", "change-me-device");
        await app.MarkNotificationBoundaryAsync();

        await RequestChangeAsync(app, session.AccessToken, "changed@example.com");

        // The proof goes to the address being claimed; the address being left is told but is given
        // nothing it could act on.
        var verifyMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeVerify
            && message.Email == "changed@example.com");
        verifyMail.Token.Should().NotBeNullOrWhiteSpace();

        var noticeMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeRequested
            && message.Email == "change-me@example.com");
        noticeMail.Token.Should().BeNullOrWhiteSpace();
        noticeMail.NewEmail.Should().Be("changed@example.com");

        // Nothing has moved yet.
        (await app.FindUserByEmailAsync("change-me@example.com")).Should().NotBeNull();

        var confirm = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest { Token = verifyMail.Token });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await app.DescribeFailureAsync(confirm));

        (await app.FindUserByEmailAsync("changed@example.com")).Should().NotBeNull();
        (await app.FindUserByEmailAsync("change-me@example.com")).Should().BeNull();
    }

    /// <summary>
    /// The address is an access token claim, so confirming has to invalidate every token that
    /// still carries the old one.
    /// </summary>
    [Fact]
    public async Task Confirm_ShouldRejectTokensIssuedBeforeTheChange()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "stale-token@example.com", "stale-device");
        await app.MarkNotificationBoundaryAsync();

        // The session works before the change.
        (await app.GetWithBearerAsync("/api/profile", session.AccessToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await RequestChangeAsync(app, session.AccessToken, "stale-token-new@example.com");
        var verifyMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeVerify
            && message.Email == "stale-token-new@example.com");

        var confirm = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest { Token = verifyMail.Token });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await app.DescribeFailureAsync(confirm));

        (await app.GetWithBearerAsync("/api/profile", session.AccessToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_ShouldAcceptTheEmailedCode()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "otp-change@example.com", "otp-device");
        await app.MarkNotificationBoundaryAsync();

        var challenge = await RequestChangeAsync(app, session.AccessToken, "otp-changed@example.com");
        var verifyMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeVerify
            && message.Email == "otp-changed@example.com");

        var confirm = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest
            {
                Code = verifyMail.Code,
                Challenge = challenge.Challenge
            });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await app.DescribeFailureAsync(confirm));
        (await app.FindUserByEmailAsync("otp-changed@example.com")).Should().NotBeNull();
    }

    [Fact]
    public async Task Request_ShouldRejectAnAddressAnotherAccountHolds()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        await app.SeedUserAsync("occupied@example.com", username: "occupier");
        var session = await SignInAsync(app, "wants-occupied@example.com", "occupied-device");

        var response = await app.PostJsonWithBearerAndCsrfAsync(
            "/api/profile/email",
            new ChangeEmailRequest
            {
                NewEmail = "occupied@example.com",
                CurrentPassword = "Password123!"
            },
            session.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Request_ShouldRejectAnIncorrectPassword()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "wrong-pw@example.com", "wrong-pw-device");

        var response = await app.PostJsonWithBearerAndCsrfAsync(
            "/api/profile/email",
            new ChangeEmailRequest
            {
                NewEmail = "wrong-pw-new@example.com",
                CurrentPassword = "NotMyPassword!"
            },
            session.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The endpoint sends mail to an address the caller picked, so MFA step-up is what stands
    /// between it and anyone holding a stolen access token.
    /// </summary>
    [Fact]
    public async Task Request_ShouldRequireStepUpVerification()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var user = await app.SeedUserAsync("no-stepup@example.com", username: "no-stepup");
        await app.SeedKnownDeviceAsync(user.Id, "no-stepup-device");
        var session = await app.LoginApiAsync("no-stepup", trustedDeviceToken: "no-stepup-device");

        // Deliberately no CompleteSessionMfaByEmailAsync.
        var response = await app.PostJsonWithBearerAndCsrfAsync(
            "/api/profile/email",
            new ChangeEmailRequest
            {
                NewEmail = "no-stepup-new@example.com",
                CurrentPassword = "Password123!"
            },
            session.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PendingEndpoints_ShouldReportAndCancelTheRequest()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "pending@example.com", "pending-device");
        await app.MarkNotificationBoundaryAsync();

        await RequestChangeAsync(app, session.AccessToken, "pending-new@example.com");

        var pending = await app.GetWithBearerAsync("/api/profile/email/pending", session.AccessToken);
        pending.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await app.ReadApiResponseAsync<PendingEmailChangeResponse>(pending);
        body.Data!.NewEmail.Should().Be("pending-new@example.com");

        var verifyMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeVerify
            && message.Email == "pending-new@example.com");

        var cancel = await DeleteWithBearerAndCsrfAsync(
            app,
            "/api/profile/email/pending",
            session.AccessToken);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterCancel = await app.GetWithBearerAsync(
            "/api/profile/email/pending",
            session.AccessToken);
        (await app.ReadApiResponseAsync<PendingEmailChangeResponse>(afterCancel))
            .Data.Should().BeNull();

        // A cancelled request must not leave a live link behind.
        var confirm = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest { Token = verifyMail.Token });
        confirm.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_ShouldRejectAReusedToken()
    {
        await using var app = await AuthApiTestApp.CreateAsync();
        var session = await SignInAsync(app, "replay@example.com", "replay-device");
        await app.MarkNotificationBoundaryAsync();

        await RequestChangeAsync(app, session.AccessToken, "replay-new@example.com");
        var verifyMail = await app.WaitForEmailAsync(message =>
            message.Type == EmailMessageType.EmailChangeVerify
            && message.Email == "replay-new@example.com");

        var first = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest { Token = verifyMail.Token });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest { Token = verifyMail.Token });
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_ShouldRejectARequestWithNeitherProof()
    {
        await using var app = await AuthApiTestApp.CreateAsync();

        var response = await app.PostJsonWithCsrfAsync(
            "/api/auth/verify/email-change",
            new EmailChangeConfirmationRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<AuthenticatedSessionResponse> SignInAsync(
        AuthApiTestApp app,
        string email,
        string deviceToken)
    {
        var username = email.Split('@')[0];
        var user = await app.SeedUserAsync(email, username: username);
        await app.SeedKnownDeviceAsync(user.Id, deviceToken);
        var session = await app.LoginApiAsync(username, trustedDeviceToken: deviceToken);
        await app.CompleteSessionMfaByEmailAsync(email, session.AccessToken);
        return session;
    }

    private static async Task<VerificationChallengeResponse> RequestChangeAsync(
        AuthApiTestApp app,
        string accessToken,
        string newEmail)
    {
        var response = await app.PostJsonWithBearerAndCsrfAsync(
            "/api/profile/email",
            new ChangeEmailRequest { NewEmail = newEmail, CurrentPassword = "Password123!" },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await app.DescribeFailureAsync(response));
        return (await app.ReadApiResponseAsync<VerificationChallengeResponse>(response)).Data!;
    }

    private static async Task<HttpResponseMessage> DeleteWithBearerAndCsrfAsync(
        AuthApiTestApp app,
        string path,
        string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add(CsrfConfiguration.CsrfHeaderName, await app.GetCsrfTokenAsync());
        return await app.Client.SendAsync(request);
    }
}
