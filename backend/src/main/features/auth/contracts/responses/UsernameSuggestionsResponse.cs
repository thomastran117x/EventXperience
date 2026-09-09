namespace backend.main.features.auth.contracts.responses;

/// <summary>
/// A batch of suggested usernames for the signup and rename forms.
/// </summary>
public sealed class UsernameSuggestionsResponse
{
    /// <summary>
    /// The suggestions, free at the moment they were drawn. May hold fewer than the usual three,
    /// or none: suggestions are a convenience, so an exhausted draw returns a short list rather
    /// than failing the request, and the form works without them.
    /// </summary>
    public required IReadOnlyList<UsernameSuggestionResponse> Suggestions
    {
        get; set;
    }
}
