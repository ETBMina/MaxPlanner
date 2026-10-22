namespace MaxPlanner.Application.Common;

public static class TimeProviderExtensions
{
    /// <summary>
    /// The user's local calendar day (B-04), read from the provider's local clock. Never derived from
    /// UTC midnight, so date automation is independent of the offset and DST.
    /// </summary>
    public static DateOnly LocalToday(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
    }
}
