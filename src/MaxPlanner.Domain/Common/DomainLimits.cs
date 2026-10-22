namespace MaxPlanner.Domain.Common;

/// <summary>
/// B-02.10 design-default limits. Lengths count UTF-16 code units, matching what text inputs and
/// the SQLite TEXT columns measure; violations are reported, never truncated.
/// </summary>
public static class DomainLimits
{
    public const int Title = 250;
    public const int Subtitle = 250;
    public const int CrNumber = 100;
    public const int TaskNumber = 100;
    public const int LookupName = 100;
    public const int Service = 250;
    public const int EmailSubject = 1_000;
    public const int Url = 2_048;
    public const int LongText = 100_000;
    public const int ChecklistText = 2_000;
    public const int ChecklistItemsPerTask = 1_000;
    public const int SubtaskCardsPerTask = 500;

    /// <summary>Custom-field key length; keys are identifiers, so the lookup-name limit applies.</summary>
    public const int FieldKey = LookupName;

    /// <summary>
    /// Single-line custom Text values. B-02.10 sets no figure for them; this design default reuses the
    /// longest built-in single-line limit (EmailSubject). MultilineText uses <see cref="LongText"/>.
    /// </summary>
    public const int CustomText = EmailSubject;
}
