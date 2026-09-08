using System.ComponentModel.DataAnnotations.Schema;

namespace backend.main.features.profile;

public class User
{
    public int Id
    {
        get; set;
    }
    public required string Email
    {
        get; set;
    }
    public string? Password
    {
        get; set;
    }

    /// <summary>
    /// Whether the account has a password of its own. Not persisted: reads that sanitize
    /// <see cref="Password"/> away still need to say whether one exists, because linking a
    /// provider does not remove it — an account can hold both.
    /// </summary>
    [NotMapped]
    public bool HasLocalPassword
    {
        get; set;
    }
    public required string Usertype
    {
        get; set;
    }
    public string? Name
    {
        get; set;
    }
    public string? Username
    {
        get; set;
    }
    public DateTime? UsernameChangeAvailableAtUtc
    {
        get; set;
    }
    public string? Avatar
    {
        get; set;
    }
    public string? Address
    {
        get; set;
    }
    public string? Phone
    {
        get; set;
    }
    public string? MicrosoftID
    {
        get; set;
    }
    public string? GoogleID
    {
        get; set;
    }
    public bool IsDisabled
    {
        get; set;
    } = false;
    public DateTime? DisabledAtUtc
    {
        get; set;
    }
    public string? DisabledReason
    {
        get; set;
    }
    public int AuthVersion
    {
        get; set;
    } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

