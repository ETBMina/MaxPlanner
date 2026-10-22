using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Fields;

namespace MaxPlanner.Infrastructure.Persistence.Mapping;

/// <summary>
/// Stored spellings of enum values. They are part of the file format (and of CHECK constraints in
/// M0001), so they are mapped explicitly instead of relying on enum member names that may be renamed.
/// </summary>
internal static class StorageNames
{
    public static string Of(EntityKind kind) => kind switch
    {
        EntityKind.Task => "Task",
        EntityKind.ChecklistItem => "ChecklistItem",
        EntityKind.SubtaskCard => "SubtaskCard",
        EntityKind.ChangeRequest => "ChangeRequest",
        EntityKind.ReviewItem => "ReviewItem",
        EntityKind.GeneralTodo => "GeneralTodo",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(FieldTarget target) => target switch
    {
        FieldTarget.Task => "Task",
        FieldTarget.ChangeRequest => "ChangeRequest",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public static string Of(FieldKind kind) => kind switch
    {
        FieldKind.Text => "Text",
        FieldKind.MultilineText => "MultilineText",
        FieldKind.Number => "Number",
        FieldKind.Date => "Date",
        FieldKind.Lookup => "Lookup",
        FieldKind.People => "People",
        FieldKind.Tags => "Tags",
        FieldKind.Url => "Url",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static FieldTarget ParseTarget(string value) => value switch
    {
        "Task" => FieldTarget.Task,
        "ChangeRequest" => FieldTarget.ChangeRequest,
        _ => throw new CorruptStoredDataException("field_definitions", "target", "unknown target"),
    };

    public static FieldKind ParseKind(string value) => value switch
    {
        "Text" => FieldKind.Text,
        "MultilineText" => FieldKind.MultilineText,
        "Number" => FieldKind.Number,
        "Date" => FieldKind.Date,
        "Lookup" => FieldKind.Lookup,
        "People" => FieldKind.People,
        "Tags" => FieldKind.Tags,
        "Url" => FieldKind.Url,
        _ => throw new CorruptStoredDataException("field_definitions", "kind", "unknown kind"),
    };

    public const string Designer = "Designer";
    public const string Developer = "Developer";
}
