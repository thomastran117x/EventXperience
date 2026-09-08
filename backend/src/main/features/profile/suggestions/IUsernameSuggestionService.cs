namespace backend.main.features.profile.suggestions;

/// <summary>
/// A username the signup and rename forms can offer, in both the form that is claimed and the form
/// that is shown.
/// </summary>
/// <param name="Username">The lowercase key, ready to be checked and claimed.</param>
/// <param name="Display">The same name as it should be rendered, e.g. <c>SmartCat23</c>.</param>
public readonly record struct UsernameSuggestion(string Username, string Display);

/// <summary>
/// Generates free-looking usernames of the form adjective + noun + number.
/// </summary>
public interface IUsernameSuggestionService
{
    /// <summary>
    /// Draws up to three suggestions that were free when they were drawn.
    /// </summary>
    /// <remarks>
    /// <b>Advisory, never a reservation.</b> Two callers can be handed the same name at the same
    /// moment, and a name offered here can be claimed a moment later. The authoritative check on the
    /// claiming path and the <c>IX_Users_Username</c> unique index are what actually decide, exactly
    /// as they do for <c>GET /auth/username/availability</c>.
    ///
    /// Returns fewer than three — possibly none — rather than throwing when the draw comes up empty.
    /// Suggestions are a convenience on a form that works without them, so a short list must degrade
    /// to no chips, not to a failed signup.
    /// </remarks>
    Task<IReadOnlyList<UsernameSuggestion>> SuggestAsync(CancellationToken cancellationToken = default);
}
