using System.ComponentModel.DataAnnotations;

namespace backend.main.features.events.recentlyviewed.contracts.requests
{
    /// <summary>
    /// The browser-held history being synced up at login. Every field is client-supplied and
    /// therefore untrusted: the service clamps timestamps and re-checks visibility on each id.
    /// </summary>
    public class MergeRecentlyViewedRequest : IValidatableObject
    {
        public List<MergeRecentlyViewedItem> Items { get; set; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Items.Count > 50)
            {
                yield return new ValidationResult(
                    "A merge may carry at most 50 items.",
                    new[] { nameof(Items) });
            }

            var duplicateIds = Items
                .GroupBy(i => i.EventId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Count > 0)
            {
                yield return new ValidationResult(
                    $"Duplicate event IDs are not allowed: {string.Join(", ", duplicateIds)}.",
                    new[] { nameof(Items) });
            }
        }
    }

    public class MergeRecentlyViewedItem
    {
        [Range(1, int.MaxValue)]
        public int EventId
        {
            get; set;
        }

        public DateTime ViewedAtUtc
        {
            get; set;
        }
    }
}
