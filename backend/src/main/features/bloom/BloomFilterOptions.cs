using System.ComponentModel.DataAnnotations;

namespace backend.main.features.bloom;

/// <summary>
/// Bound from the <c>BloomFilters</c> configuration section.
/// </summary>
public sealed class BloomFilterOptions : IValidatableObject
{
    /// <summary>How often each filter re-reads the shared bitmap and checks for a generation flip.</summary>
    [Range(5, 3600)]
    public int RefreshIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How often a filter is rebuilt from the database. A rebuild sheds bits left behind by
    /// deleted users and expired username reservations, which a bloom filter cannot unset.
    /// </summary>
    [Range(1, 168)]
    public int RebuildIntervalHours { get; set; } = 6;

    /// <summary>
    /// How long a superseded generation's bitmap is kept after a flip, so instances that have
    /// not yet noticed the new generation keep reading a valid key.
    /// </summary>
    [Range(1, 1440)]
    public int RetiredGenerationTtlMinutes { get; set; } = 60;

    /// <summary>
    /// How long a locally-added value is replayed onto a newly loaded generation. Covers the
    /// window between this instance writing a value and a rebuild started before that write
    /// becoming the active generation.
    /// </summary>
    [Range(1, 1440)]
    public int LocalReplayWindowMinutes { get; set; } = 30;

    /// <summary>
    /// Minimum gap between rebuilds triggered by a failed shared write. Without it, a sustained
    /// Redis outage would fail every write and schedule a full table scan on every refresh tick.
    /// </summary>
    [Range(1, 1440)]
    public int ForcedRebuildCooldownMinutes { get; set; } = 15;

    /// <summary>
    /// Per-target sizing, keyed by the names in <see cref="BloomFilterTargets"/>. Defaults to the
    /// username filter; adding club names or emails is a configuration entry plus a matching
    /// <see cref="IBloomFilterSource"/> registration.
    /// </summary>
    public Dictionary<string, BloomFilterTargetOptions> Targets
    {
        get; set;
    } =
        new(StringComparer.Ordinal)
        {
            // Every registered source needs a matching target here, not just in appsettings.json.
            // A source with no configured filter is silent: the rebuild runner warns once a cycle
            // and every lookup degrades to a database query, so the feature simply never turns on.
            [BloomFilterTargets.Username] = new(),
            [BloomFilterTargets.Email] = new(),
        };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, target) in Targets)
        {
            if (!BloomFilterTargets.All.Contains(name))
            {
                yield return new ValidationResult(
                    $"Unknown bloom filter target '{name}'. Known targets: {string.Join(", ", BloomFilterTargets.All)}.",
                    [nameof(Targets)]);
                continue;
            }

            foreach (var result in target.Validate(name))
                yield return result;
        }
    }
}

public sealed class BloomFilterTargetOptions
{
    /// <summary>Number of distinct values the filter is sized for.</summary>
    public long ExpectedItems { get; set; } = 100_000;

    /// <summary>
    /// Target false-positive rate. A false positive costs one database query, so this trades
    /// memory against how often the filter fails to save a round trip.
    /// </summary>
    public double FalsePositiveRate { get; set; } = 0.01;

    internal IEnumerable<ValidationResult> Validate(string name)
    {
        if (ExpectedItems is <= 0 or > 1_000_000_000)
        {
            yield return new ValidationResult(
                $"BloomFilters:Targets:{name}:ExpectedItems must be between 1 and 1,000,000,000.",
                [nameof(ExpectedItems)]);
        }

        if (double.IsNaN(FalsePositiveRate) || FalsePositiveRate <= 0 || FalsePositiveRate >= 1)
        {
            yield return new ValidationResult(
                $"BloomFilters:Targets:{name}:FalsePositiveRate must be between 0 and 1, exclusive.",
                [nameof(FalsePositiveRate)]);
        }
    }
}
