namespace backend.main.features.auth.contracts.responses;

/// <summary>
/// One suggested username, in both the form that is claimed and the form that is shown.
/// </summary>
/// <remarks>
/// Both forms are returned rather than just the display string because the client needs each for a
/// different job: <see cref="Display"/> labels the chip and becomes the field value, while
/// <see cref="Username"/> is what the availability probe echoes back and what the client compares
/// against. Deriving one from the other on the client would put the casing rule in a second place.
/// </remarks>
public sealed class UsernameSuggestionResponse
{
    /// <summary>The normalised, claimable form, e.g. <c>smartcat23</c>.</summary>
    public required string Username
    {
        get; set;
    }

    /// <summary>The same name as it should be rendered, e.g. <c>SmartCat23</c>.</summary>
    public required string Display
    {
        get; set;
    }
}
