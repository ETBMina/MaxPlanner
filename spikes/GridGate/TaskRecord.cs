namespace GridGate;

public enum TaskKind
{
    Task,
    SubtaskCard,
}

/// <summary>Immutable committed read model for one Grid row. Replaced as a whole after a successful save.</summary>
public sealed record TaskRecord(
    Guid Id,
    TaskKind Kind,
    string? CrNumber,
    string? TaskNumber,
    string Squad,
    string Owner,
    string Title,
    bool IsLinked,
    string Service,
    string Status,
    string Priority,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Notes,
    string Tags,
    DateTime CreatedAtUtc);

public static class GridColumnKeys
{
    public const string Kind = "Kind";
    public const string CrNumber = "CrNumber";
    public const string TaskNumber = "TaskNumber";
    public const string Squad = "Squad";
    public const string Owner = "Owner";
    public const string Title = "Title";
    public const string Service = "Service";
    public const string Status = "Status";
    public const string Priority = "Priority";
    public const string StartDate = "StartDate";
    public const string EndDate = "EndDate";
    public const string Notes = "Notes";
    public const string Tags = "Tags";
}

public static class Lookups
{
    // "Fail on save (test)" lets the ComboBox editor exercise the failure path.
    public static IReadOnlyList<string> Statuses { get; } = ["Backlog", "In Progress", "Review", "Done", "Fail on save (test)"];

    public static IReadOnlyList<string> Priorities { get; } = ["Low", "Medium", "High", "Critical"];
}
