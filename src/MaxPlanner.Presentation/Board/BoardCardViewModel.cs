using System.Globalization;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Board;

/// <summary>Chip tokens assigned by configured priority rank, never by name (design §6.1).</summary>
public enum PriorityLevel
{
    Level1,
    Level2,
    Mid,
    Lowest,
}

/// <summary>
/// A Board card for a task or a Subtask card (B-07, B-16, design §4.2 anatomy). Read-only in Phase 2: moves,
/// the card menu, and checklist checkboxes on cards arrive in Phase 3. Use on the UI thread.
/// </summary>
public sealed class BoardCardViewModel : ProjectionItemViewModel<WorkItemRecord, Guid>
{
    private readonly ILocalizedStrings _strings;
    private readonly Func<WorkItemRecord, PriorityLevel> _priorityLevel;

    public BoardCardViewModel(WorkItemRecord model, ILocalizedStrings strings, Func<WorkItemRecord, PriorityLevel> priorityLevel)
        : base(model)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _priorityLevel = priorityLevel ?? throw new ArgumentNullException(nameof(priorityLevel));
    }

    public override Guid Key => Model.Id;

    public bool IsTask => Model.Kind == BoardCardKind.Task;

    public bool IsSubtaskCard => !IsTask;

    /// <summary>The effective (possibly inherited) title for a task; the subtitle for a Subtask card.</summary>
    public string Title => Model.Task?.EffectiveTitle ?? Model.Card!.Subtitle;

    /// <summary>"Subtask of Card limits API" (B-16.5).</summary>
    public string SubtaskOfText => Model.Card is { } card ? _strings.Format("Board_SubtaskOf", card.ParentTitle) : string.Empty;

    public string Service => (Model.Task is { } task ? task.Service : Model.Card!.ParentService) ?? string.Empty;

    public bool HasService => Service.Length > 0;

    /// <summary>"CR-1067 · Task 00512", or empty for an unlinked task (T-13: inherited, read-only).</summary>
    public string CrReference => Model.Cr.CrNumber is not { } number
        ? string.Empty
        : Model.Cr.TaskNumber is { } taskNumber ? _strings.Format("Board_CrReferenceWithTask", number, taskNumber) : number;

    public bool HasCrReference => CrReference.Length > 0;

    public string PriorityText => Model.Task?.Priority.Name ?? string.Empty;

    public PriorityLevel PriorityLevel => _priorityLevel(Model);

    public string PriorityName => _strings.Format("Board_PriorityName", PriorityText);

    /// <summary>D15: the inherited squad, "General" when there is none.</summary>
    public string SquadText => Model.Cr.SquadName;

    public string OwnerText => Model.Cr.OwnerName ?? string.Empty;

    public bool HasOwner => OwnerText.Length > 0;

    public bool HasChecklist => Model.Task is { ChecklistTotal: > 0 };

    /// <summary>"Checklist 1/3", or "No checklist items" so an empty list never looks complete (B-07).</summary>
    public string ChecklistText => Model.Task is { } task && task.ChecklistTotal > 0
        ? _strings.Format("Board_ChecklistProgress", task.ChecklistDone, task.ChecklistTotal)
        : _strings.GetString("Board_NoChecklistItems");

    public double ChecklistPercent => Model.Task is { ChecklistTotal: > 0 } task ? 100.0 * task.ChecklistDone / task.ChecklistTotal : 0;

    public bool HasSubtaskCards => Model.Task is { SubtaskCardCount: > 0 };

    public string SubtaskCardsText => Model.Task is { } task
        ? _strings.Format(task.SubtaskCardCount == 1 ? "Board_SubtaskCardCountOne" : "Board_SubtaskCardCount", task.SubtaskCardCount)
        : string.Empty;

    public string DatesText
    {
        get
        {
            var (start, end) = Model.Task is { } task ? (task.StartDate, task.EndDate) : (Model.Card!.StartDate, Model.Card.EndDate);
            return (start, end) switch
            {
                (null, null) => string.Empty,
                ({ } s, null) => _strings.Format("Board_DatesStarted", Format(s)),
                (null, { } e) => _strings.Format("Board_DatesEnded", Format(e)),
                ({ } s, { } e) => _strings.Format("Board_DatesRange", Format(s), Format(e)),
            };
        }
    }

    public bool HasDates => DatesText.Length > 0;

    /// <summary>The concise card summary of design §8.2, used as the card's accessible name.</summary>
    public string AccessibleName => IsTask
        ? _strings.Format("Board_TaskCardName", Title, HasService ? Service : _strings.GetString("Board_NoService"),
            HasCrReference ? CrReference : _strings.GetString("Board_Unlinked"), PriorityText, SquadText, ChecklistText)
        : _strings.Format("Board_SubtaskCardName", Title, Model.Card!.ParentTitle, Model.Status.Name);

    protected override void OnModelUpdated(WorkItemRecord previous)
    {
        // Every displayed value derives from the model; one change notification per property keeps bound
        // containers in place (spike 5) instead of replacing the item.
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsSubtaskCard));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubtaskOfText));
        OnPropertyChanged(nameof(Service));
        OnPropertyChanged(nameof(HasService));
        OnPropertyChanged(nameof(CrReference));
        OnPropertyChanged(nameof(HasCrReference));
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(PriorityLevel));
        OnPropertyChanged(nameof(PriorityName));
        OnPropertyChanged(nameof(SquadText));
        OnPropertyChanged(nameof(OwnerText));
        OnPropertyChanged(nameof(HasOwner));
        OnPropertyChanged(nameof(HasChecklist));
        OnPropertyChanged(nameof(ChecklistText));
        OnPropertyChanged(nameof(ChecklistPercent));
        OnPropertyChanged(nameof(HasSubtaskCards));
        OnPropertyChanged(nameof(SubtaskCardsText));
        OnPropertyChanged(nameof(DatesText));
        OnPropertyChanged(nameof(HasDates));
        OnPropertyChanged(nameof(AccessibleName));
    }

    private static string Format(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);
}
