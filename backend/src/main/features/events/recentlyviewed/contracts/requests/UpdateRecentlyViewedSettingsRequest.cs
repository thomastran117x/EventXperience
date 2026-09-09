using System.ComponentModel.DataAnnotations;

namespace backend.main.features.events.recentlyviewed.contracts.requests
{
    public class UpdateRecentlyViewedSettingsRequest
    {
        /// <summary>
        /// Nullable so an absent field fails validation instead of silently binding to false —
        /// a malformed body must not switch tracking off on the user's behalf.
        /// </summary>
        [Required(ErrorMessage = "Enabled is required.")]
        public bool? Enabled
        {
            get; set;
        }
    }
}
