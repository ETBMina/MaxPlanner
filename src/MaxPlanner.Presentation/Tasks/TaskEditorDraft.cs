using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>A checklist row in the task editor (design §5.1 group 7). Text and completion are part of the draft.</summary>
public sealed partial class EditorChecklistItemViewModel : ObservableObject
{
    public EditorChecklistItemViewModel(ChecklistItemId id, string text, bool isCompleted, bool isNew)
    {
        Id = id;
        Text = text;
        IsCompleted = isCompleted;
        IsNew = isNew;
    }

    public ChecklistItemId Id { get; }

    /// <summary>Added in this draft; it has no saved card to pop out yet.</summary>
    public bool IsNew { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }
}

/// <summary>A committed Subtask card of the edited task (design §5.1 group 8); cards are created immediately, not drafted.</summary>
public sealed record EditorSubtaskCardViewModel(SubtaskCardId Id, string Subtitle, string StatusName, string DatesText);

/// <summary>One row of the conflict review step (design §5.10): Field | Your draft | Saved now, with Use mine.</summary>
public sealed partial class ConflictRowViewModel : ObservableObject
{
    public ConflictRowViewModel(string key, string field, string mine, string saved)
    {
        Key = key;
        Field = field;
        Mine = mine;
        Saved = saved;
    }

    /// <summary>The draft field, for example <c>Title</c>; stable, never display text.</summary>
    public string Key { get; }

    public string Field { get; }

    public string Mine { get; }

    public string Saved { get; }

    /// <summary>Keeps the draft's value when the draft is refreshed; off takes the saved value.</summary>
    [ObservableProperty]
    public partial bool UseMine { get; set; } = true;
}

/// <summary>The inline steps of the task editor (design §5.0: nested decisions never open a second dialog).</summary>
public enum EditorStep
{
    Form,
    LinkPick,
    LinkConfirm,
    ConflictReview,
    ReloadConfirm,
}

/// <summary>What the editor's More menu asked for after it closes (design §5.0: dialogs never stack).</summary>
public enum EditorFollowUp
{
    None,
    Copy,
    Delete,
    OpenCr,
    OpenSubtaskCard,
    AddSubtaskCard,
}

/// <summary>
/// The editable values of a task draft, compared field by field to tell whether the draft is dirty and
/// which fields a conflict review lists. Lists compare by sequence.
/// </summary>
internal sealed record DraftValues(
    string Title,
    string Service,
    TaskStatusId? Status,
    PriorityId? Priority,
    Guid? Designer,
    Guid? Developer,
    IReadOnlyList<Guid> Tags,
    DateOnly? Start,
    DateOnly? End,
    string Notes,
    IReadOnlyList<(ChecklistItemId Id, string Text, bool IsCompleted)> Checklist,
    string CustomFields)
{
    public static IReadOnlyList<string> Keys { get; } =
        ["Title", "Service", "Status", "Priority", "Designer", "Developer", "Tags", "StartDate", "EndDate", "Notes", "ChecklistItems", "CustomFields"];

    public bool SameAs(DraftValues other, string key) => key switch
    {
        "Title" => Title == other.Title,
        "Service" => Service == other.Service,
        "Status" => Status == other.Status,
        "Priority" => Priority == other.Priority,
        "Designer" => Designer == other.Designer,
        "Developer" => Developer == other.Developer,
        "Tags" => Tags.SequenceEqual(other.Tags),
        "StartDate" => Start == other.Start,
        "EndDate" => End == other.End,
        "Notes" => Notes == other.Notes,
        "ChecklistItems" => Checklist.SequenceEqual(other.Checklist),
        "CustomFields" => CustomFields == other.CustomFields,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    public bool SameAs(DraftValues other) => Keys.All(k => SameAs(other, k));
}
