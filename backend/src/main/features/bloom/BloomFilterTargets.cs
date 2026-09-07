namespace backend.main.features.bloom;

/// <summary>
/// Names of the namespaces a bloom filter can front. A target name appears in Redis keys and
/// in the hash input, so renaming one invalidates its filter and must be treated as a new target.
/// </summary>
public static class BloomFilterTargets
{
    /// <summary>Usernames, including active entries in the username reservation cooldown table.</summary>
    public const string Username = "username";

    /// <summary>Club names. Reserved: no filter is registered until club names are made unique.</summary>
    public const string ClubName = "club-name";

    /// <summary>Account email addresses, as stored on the user row.</summary>
    public const string Email = "email";

    /// <summary>Every target name the configuration binder will accept.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Username,
        ClubName,
        Email,
    };
}
