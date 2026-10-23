namespace MaxPlanner.Domain.Common;

/// <summary>Stable error codes; Presentation maps each to a localized message.</summary>
public static class DomainErrorCodes
{
    public const string Required = "required";
    public const string TooLong = "too-long";
    public const string InvalidUrl = "invalid-url";
    public const string DateRangeInverted = "date-range-inverted";
    public const string TitleNotAllowedWhenLinked = "title-not-allowed-when-linked";
    public const string ServiceRequiredWhenLinked = "service-required-when-linked";
    public const string TaskAlreadyLinked = "task-already-linked";
    public const string ArchivedReadOnly = "archived-read-only";
    public const string TooManyItems = "too-many-items";
    public const string ItemNotFound = "item-not-found";
    public const string StatusMismatch = "status-mismatch";
    public const string CascadeScopeChanged = "cascade-scope-changed";
    public const string NotArchived = "not-archived";
    public const string UnknownReference = "unknown-reference";

    // B-11 custom fields.
    public const string InvalidKey = "invalid-key";
    public const string ReservedKey = "reserved-key";
    public const string DuplicateKey = "duplicate-key";
    public const string DuplicateName = "duplicate-name";
    public const string InvalidFieldOptions = "invalid-field-options";
    public const string UnknownField = "unknown-field";
    public const string CustomValueKindMismatch = "custom-value-kind-mismatch";
    public const string UnknownLookupOption = "unknown-lookup-option";

    // B-04 lookup removal.
    public const string LastLookupRequired = "last-lookup-required";
    public const string ReplacementRequired = "replacement-required";
    public const string ReplacementSameAsRemoved = "replacement-same-as-removed";

    /// <summary>The usage counts changed between the confirmation and the commit; the user must confirm again (B-04, design §5.9).</summary>
    public const string LookupUsageChanged = "lookup-usage-changed";

    // B-04/B-08 workflow configuration.
    /// <summary>The CR status used by "Move to backlog" must keep the Backlog flag, so moved CRs appear in the backlog (B-08.3, B-08.11).</summary>
    public const string BacklogStatusMustBeFlagged = "backlog-status-must-be-flagged";

    /// <summary>B-11: a field cannot become required while existing records have no value; it stays optional until they are filled.</summary>
    public const string RequiredFieldHasMissingValues = "required-field-has-missing-values";

    /// <summary>A Lookup option that stored values use cannot be removed from its field.</summary>
    public const string OptionInUse = "option-in-use";

    /// <summary>A stored record breaks a structural rule (e.g. revision below 1); reported by Rehydrate, never repaired.</summary>
    public const string InvalidStoredValue = "invalid-stored-value";
}
