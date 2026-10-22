using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GridGate;

/// <summary>
/// One Grid row. Editors bind only to the Draft* properties; <see cref="Committed"/> is replaced
/// as a whole and only after the save service reports success.
/// </summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    public TaskRowViewModel(TaskRecord committed)
    {
        Committed = committed;
        DraftTitle = committed.Title;
        DraftService = committed.Service;
        DraftStatus = committed.Status;
        DraftPriority = committed.Priority;
        DraftStartDate = ToPickerDate(committed.StartDate);
        DraftEndDate = ToPickerDate(committed.EndDate);
        DraftNotes = committed.Notes;
        DraftTags = committed.Tags;
    }

    public Guid Id => Committed.Id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText), nameof(StartDateText), nameof(EndDateText), nameof(NotesPreview), nameof(LockVisibility), nameof(AccessibleSummary))]
    public partial TaskRecord Committed { get; private set; }

    [ObservableProperty]
    public partial string DraftTitle { get; set; }

    [ObservableProperty]
    public partial string DraftService { get; set; }

    [ObservableProperty]
    public partial string DraftStatus { get; set; }

    [ObservableProperty]
    public partial string DraftPriority { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DraftStartDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DraftEndDate { get; set; }

    [ObservableProperty]
    public partial string DraftNotes { get; set; }

    [ObservableProperty]
    public partial string DraftTags { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorVisibility))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Fallback grid only: the column whose editor is open in this row, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleEditorVisibility), nameof(TitleDisplayVisibility), nameof(StatusEditorVisibility), nameof(StatusDisplayVisibility))]
    public partial string? EditingColumn { get; set; }

    public Microsoft.UI.Xaml.Visibility TitleEditorVisibility => VisibleWhen(EditingColumn == GridColumnKeys.Title);

    public Microsoft.UI.Xaml.Visibility TitleDisplayVisibility => VisibleWhen(EditingColumn != GridColumnKeys.Title);

    public Microsoft.UI.Xaml.Visibility StatusEditorVisibility => VisibleWhen(EditingColumn == GridColumnKeys.Status);

    public Microsoft.UI.Xaml.Visibility StatusDisplayVisibility => VisibleWhen(EditingColumn != GridColumnKeys.Status);

    public IReadOnlyList<string> StatusOptions { get; } = Lookups.Statuses;

    public IReadOnlyList<string> PriorityOptions { get; } = Lookups.Priorities;

    public string KindText => Committed.Kind == TaskKind.Task ? "Task" : "Subtask card";

    public string StartDateText => FormatDate(Committed.StartDate);

    public string EndDateText => FormatDate(Committed.EndDate);

    public string NotesPreview
    {
        get
        {
            var notes = Committed.Notes;
            var newline = notes.IndexOfAny(['\r', '\n']);
            return newline < 0 ? notes : string.Concat(notes.AsSpan(0, newline), " …");
        }
    }

    public Microsoft.UI.Xaml.Visibility LockVisibility =>
        Committed.IsLinked ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ErrorVisibility =>
        ErrorMessage is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public string AccessibleSummary =>
        string.Create(CultureInfo.CurrentCulture, $"{KindText} {Committed.Title}, {Committed.CrNumber ?? "unlinked"}, {Committed.Status}, {Committed.Priority} priority");

    /// <summary>B-07: Kind/CR/Task Number/Squad/Owner never edit; linked titles are read-only; Subtask cards edit only Status, dates, and Notes.</summary>
    public bool CanEdit(string column) => column switch
    {
        GridColumnKeys.Title => Committed.Kind == TaskKind.Task && !Committed.IsLinked,
        GridColumnKeys.Service or GridColumnKeys.Priority or GridColumnKeys.Tags => Committed.Kind == TaskKind.Task,
        GridColumnKeys.Status or GridColumnKeys.StartDate or GridColumnKeys.EndDate or GridColumnKeys.Notes => true,
        _ => false,
    };

    public object? GetDraftValue(string column) => column switch
    {
        GridColumnKeys.Title => DraftTitle,
        GridColumnKeys.Service => DraftService,
        GridColumnKeys.Status => DraftStatus,
        GridColumnKeys.Priority => DraftPriority,
        GridColumnKeys.StartDate => ToDateOnly(DraftStartDate),
        GridColumnKeys.EndDate => ToDateOnly(DraftEndDate),
        GridColumnKeys.Notes => DraftNotes,
        GridColumnKeys.Tags => DraftTags,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Column is not editable."),
    };

    private object? GetCommittedValue(string column) => column switch
    {
        GridColumnKeys.Title => Committed.Title,
        GridColumnKeys.Service => Committed.Service,
        GridColumnKeys.Status => Committed.Status,
        GridColumnKeys.Priority => Committed.Priority,
        GridColumnKeys.StartDate => Committed.StartDate,
        GridColumnKeys.EndDate => Committed.EndDate,
        GridColumnKeys.Notes => Committed.Notes,
        GridColumnKeys.Tags => Committed.Tags,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Column is not editable."),
    };

    public bool IsDraftDirty(string column) => !Equals(GetDraftValue(column), GetCommittedValue(column));

    /// <summary>Synchronous semantic validation that runs before the save is attempted.</summary>
    public string? Validate(string column)
    {
        if (column == GridColumnKeys.Title && string.IsNullOrWhiteSpace(DraftTitle))
        {
            return "Title is required.";
        }

        var start = column == GridColumnKeys.StartDate ? ToDateOnly(DraftStartDate) : Committed.StartDate;
        var end = column == GridColumnKeys.EndDate ? ToDateOnly(DraftEndDate) : Committed.EndDate;
        return (column is GridColumnKeys.StartDate or GridColumnKeys.EndDate) && start > end
            ? "End Date can't be before Start Date."
            : null;
    }

    public void ResetDraft(string column)
    {
        switch (column)
        {
            case GridColumnKeys.Title:
                DraftTitle = Committed.Title;
                break;
            case GridColumnKeys.Service:
                DraftService = Committed.Service;
                break;
            case GridColumnKeys.Status:
                DraftStatus = Committed.Status;
                break;
            case GridColumnKeys.Priority:
                DraftPriority = Committed.Priority;
                break;
            case GridColumnKeys.StartDate:
                DraftStartDate = ToPickerDate(Committed.StartDate);
                break;
            case GridColumnKeys.EndDate:
                DraftEndDate = ToPickerDate(Committed.EndDate);
                break;
            case GridColumnKeys.Notes:
                DraftNotes = Committed.Notes;
                break;
            case GridColumnKeys.Tags:
                DraftTags = Committed.Tags;
                break;
            default:
                break;
        }
    }

    public void BeginSave()
    {
        IsSaving = true;
        ErrorMessage = null;
    }

    public void FailSave(string message)
    {
        IsSaving = false;
        ErrorMessage = message;
    }

    public void ClearError() => ErrorMessage = null;

    /// <summary>Publishes the saved value as a new committed record. The draft is left as the user last typed it.</summary>
    public void CompleteSave(string column, object? savedValue)
    {
        Committed = column switch
        {
            GridColumnKeys.Title => Committed with { Title = (string)savedValue! },
            GridColumnKeys.Service => Committed with { Service = (string)savedValue! },
            GridColumnKeys.Status => Committed with { Status = (string)savedValue! },
            GridColumnKeys.Priority => Committed with { Priority = (string)savedValue! },
            GridColumnKeys.StartDate => Committed with { StartDate = (DateOnly?)savedValue },
            GridColumnKeys.EndDate => Committed with { EndDate = (DateOnly?)savedValue },
            GridColumnKeys.Notes => Committed with { Notes = (string)savedValue! },
            GridColumnKeys.Tags => Committed with { Tags = (string)savedValue! },
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Column is not editable."),
        };
        IsSaving = false;
        ErrorMessage = null;
    }

    public IComparable? GetSortKey(string column) => column switch
    {
        GridColumnKeys.Kind => KindText,
        GridColumnKeys.CrNumber => Committed.CrNumber,
        GridColumnKeys.TaskNumber => Committed.TaskNumber,
        GridColumnKeys.Squad => Committed.Squad,
        GridColumnKeys.Owner => Committed.Owner,
        GridColumnKeys.Title => Committed.Title,
        GridColumnKeys.Service => Committed.Service,
        GridColumnKeys.Status => Committed.Status,
        GridColumnKeys.Priority => Committed.Priority,
        GridColumnKeys.StartDate => Committed.StartDate,
        GridColumnKeys.EndDate => Committed.EndDate,
        GridColumnKeys.Notes => Committed.Notes,
        GridColumnKeys.Tags => Committed.Tags,
        _ => null,
    };

    private static Microsoft.UI.Xaml.Visibility VisibleWhen(bool condition) =>
        condition ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static string FormatDate(DateOnly? date) =>
        date?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;

    private static DateTimeOffset? ToPickerDate(DateOnly? date) =>
        date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)) : null;

    private static DateOnly? ToDateOnly(DateTimeOffset? value) =>
        value is { } v ? DateOnly.FromDateTime(v.Date) : null;
}
