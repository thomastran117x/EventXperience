namespace backend.main.features.events.recentlyviewed.contracts.responses
{
    /// <summary>The outcome of recording one view.</summary>
    public class RecordEventViewResponse
    {
        public int EventId
        {
            get; set;
        }

        /// <summary>
        /// False when the user has opted out. Deliberately a successful response rather than a
        /// 403: honouring a preference is not an error, and the detail page fires this call and
        /// forgets it, so an error status would only invite the client to branch on one.
        /// </summary>
        public bool Recorded
        {
            get; set;
        }

        /// <summary>Null when <see cref="Recorded"/> is false, so nothing serializes a year-0001 timestamp.</summary>
        public DateTime? ViewedAtUtc
        {
            get; set;
        }
    }
}
