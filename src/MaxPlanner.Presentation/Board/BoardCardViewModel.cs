using System.Globalization;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Board;

/// <summary>
/// A Board card for a task or a Subtask card (B-07, B-16, design §4.2 anatomy). Moves and menu commands are
/// run by <see cref="BoardPageViewModel"/>; the card only shows committed state plus the in-flight move cue.
/// Use on the UI thread.
/// </summary>
public sealed partial class BoardCardViewModel : ProjectionItemViewModel<WorkItemRecord, Guid>
{
    private readonly ILocalizedStrings _strings;
    private readonly Func<WorkItemRecord, PriorityLevel> _priorityLevel;
    private readonly Func<bool> _showTags;

    /// <param name="showTags">Settings › Appearance › Show tags on Board cards, read live; <see cref="OnShowTagsChanged"/> re-reads it.</param>
    public BoardCardViewModel(WorkItemRecord model, ILocalizedStrings strings, Func<WorkItemRecord, PriorityLevel> priorityLevel, Func<bool>? showTags = null)
        : base(model)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _priorityLevel = priorityLevel ?? throw new ArgumentNullException(nameof(priorityLevel));
        _showTags = showTags ?? (() => true);
        People = BoardCardPerson.From(model, strings);
        Tags = TagsOf(model);
    }

    public override Guid Key => Model.Id;

    /// <summary>Design §4.2: while a move is dragged or saving, the card is dimmed and captioned "Moving".</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsMoving { get; internal set; }

    /// <summary>"More actions for Card limits API" (design §8.2: icon buttons name the action and target).</summary>
    public string MenuButtonName => _strings.Format("Board_CardMenuName", Title);

    /// <summary>The task this card opens: itself, or a Subtask card's parent.</summary>
    public MaxPlanner.Domain.Identifiers.TaskId TaskId => Model.Task?.Id ?? Model.Card!.ParentTaskId;

    public bool IsTask => Model.Kind == BoardCardKind.Task;

    public bool IsSubtaskCard => !IsTask;

    /// <summary>The effective (possibly inherited) title for a task; the subtitle for a Subtask card.</summary>
    public string Title => Model.Task?.EffectiveTitle ?? Model.Card!.Subtitle;

    /// <summary>"Subtask of Card limits API" (B-16.5).</summary>
    public string SubtaskOfText => Model.Card is { } card ? _strings.Format("Board_SubtaskOf", card.ParentTitle) : string.Empty;

    public string Service => (Model.Task is { } task ? task.Service : Model.Card!.ParentService) ?? string.Empty;

    public bool HasService => Service.Length > 0;

    /// <summary>"CR-1067 · Task 00512", or empty for an unlinked task (T-13: inherited, read-only); the accessible name uses it.</summary>
    public string CrReference => Model.Cr.CrNumber is not { } number
        ? string.Empty
        : Model.Cr.TaskNumber is { } taskNumber ? _strings.Format("Board_CrReferenceWithTask", number, taskNumber) : number;

    /// <summary>Linked: the CR chip shows (design §4.2 item 3).</summary>
    public bool HasCrReference => Model.Cr.CrNumber is not null;

    /// <summary>"RDC-166": the CR chip text.</summary>
    public string CrNumber => Model.Cr.CrNumber ?? string.Empty;

    /// <summary>"Open CR-1067": the CR chip's accessible name and tooltip (design §4.2 proposed notes).</summary>
    public string OpenCrName => Model.Cr.CrNumber is { } number ? _strings.Format("TaskEditor_OpenCrName", number) : string.Empty;

    /// <summary>A linked task with a task number shows the Task chip, which copies the card's ID (design §4.2 item 3).</summary>
    public bool HasTaskNumber => HasCrReference && !string.IsNullOrEmpty(Model.Cr.TaskNumber);

    /// <summary>"INT-7625": the Task chip text.</summary>
    public string TaskNumber => HasTaskNumber ? Model.Cr.TaskNumber! : string.Empty;

    /// <summary>"Copy ID for Task INT-7625": the Task chip's accessible name and tooltip.</summary>
    public string CopyIdName => HasTaskNumber ? _strings.Format("Board_CopyIdChipName", TaskNumber) : string.Empty;

    public string PriorityText => Model.Task?.Priority.Name ?? string.Empty;

    public PriorityLevel PriorityLevel => _priorityLevel(Model);

    public string PriorityName => _strings.Format("Board_PriorityName", PriorityText);

    /// <summary>D15: the inherited squad, "General" when there is none.</summary>
    public string SquadText => Model.Cr.SquadName;

    /// <summary>
    /// The avatars (design §4.2 item 6): each assigned person once, in role order CR Owner, Designer, Developer.
    /// Replaced only when the people or their roles change, so other updates leave the avatars alone.
    /// </summary>
    public IReadOnlyList<BoardCardPerson> People { get; private set; }

    public bool HasPeople => People.Count > 0;

    /// <summary>The Subtask card's last row shows when it has dates or the inherited CR Owner.</summary>
    public bool HasSubtaskFooter => HasDates || HasPeople;

    /// <summary>
    /// The tag strip's tags (design §4.2 item 2): the task's own Tags, or a Subtask card's parent Tags, in tag order.
    /// Replaced only when a tag or its name changes, so other updates keep the strip as it is.
    /// </summary>
    public IReadOnlyList<TagPillItem> Tags { get; private set; }

    /// <summary>The strip shows when the setting is on and there are tags; a card without tags has no empty band.</summary>
    public bool HasTagStrip => Tags.Count > 0 && _showTags();

    /// <summary>"Integration, Zain": the strip's tooltip lists every tag.</summary>
    public string TagsText => string.Join(_strings.GetString("Board_TagSeparator"), Tags.Select(t => t.Name));

    public bool HasChecklist => Model.Task is { ChecklistTotal: > 0 };

    /// <summary>Every item done: the double-check glyph and the success color, so color is never the only cue (design §4.2 item 6).</summary>
    public bool IsChecklistComplete => Model.Task is { ChecklistTotal: > 0 } task && task.ChecklistDone == task.ChecklistTotal;

    public bool IsChecklistIncomplete => HasChecklist && !IsChecklistComplete;

    /// <summary>"1/3" beside the checklist glyph; empty without items.</summary>
    public string ChecklistCountText => Model.Task is { ChecklistTotal: > 0 } task
        ? _strings.Format("Board_ChecklistCount", task.ChecklistDone, task.ChecklistTotal)
        : string.Empty;

    /// <summary>"Checklist 1/3" (the accessible name and the bar's name), or "No checklist" so an empty list never looks complete (B-07).</summary>
    public string ChecklistText => Model.Task is { } task && task.ChecklistTotal > 0
        ? _strings.Format("Board_ChecklistProgress", task.ChecklistDone, task.ChecklistTotal)
        : _strings.GetString("Board_NoChecklist");

    public double ChecklistPercent => Model.Task is { ChecklistTotal: > 0 } task ? 100.0 * task.ChecklistDone / task.ChecklistTotal : 0;

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

    /// <summary>The concise card summary of design §8.2, used as the card's accessible name; it adds the tags while the strip shows.</summary>
    public string AccessibleName
    {
        get
        {
            var name = IsTask
                ? _strings.Format("Board_TaskCardName", Title, HasService ? Service : _strings.GetString("Board_NoService"),
                    HasCrReference ? CrReference : _strings.GetString("Board_Unlinked"), PriorityText, SquadText, ChecklistText)
                : _strings.Format("Board_SubtaskCardName", Title, Model.Card!.ParentTitle, Model.Status.Name);
            return HasTagStrip ? _strings.Format("Board_CardNameWithTags", name, TagsText) : name;
        }
    }

    /// <summary>Re-reads the Show tags setting; the Board calls it on every card when the setting changes.</summary>
    public void OnShowTagsChanged()
    {
        OnPropertyChanged(nameof(HasTagStrip));
        OnPropertyChanged(nameof(AccessibleName));
    }

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
        OnPropertyChanged(nameof(CrNumber));
        OnPropertyChanged(nameof(OpenCrName));
        OnPropertyChanged(nameof(HasTaskNumber));
        OnPropertyChanged(nameof(TaskNumber));
        OnPropertyChanged(nameof(CopyIdName));
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(PriorityLevel));
        OnPropertyChanged(nameof(PriorityName));
        OnPropertyChanged(nameof(SquadText));
        UpdatePeople();
        UpdateTags();
        OnPropertyChanged(nameof(HasChecklist));
        OnPropertyChanged(nameof(IsChecklistComplete));
        OnPropertyChanged(nameof(IsChecklistIncomplete));
        OnPropertyChanged(nameof(ChecklistCountText));
        OnPropertyChanged(nameof(ChecklistText));
        OnPropertyChanged(nameof(ChecklistPercent));
        OnPropertyChanged(nameof(DatesText));
        OnPropertyChanged(nameof(HasDates));
        OnPropertyChanged(nameof(HasSubtaskFooter));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(MenuButtonName));
    }

    // A plain array: it reaches TagPillStrip.Items, which keeps it as one object.
    private static TagPillItem[] TagsOf(WorkItemRecord record) =>
        [.. (record.Task?.Tags ?? record.Card!.ParentTags).Select(t => new TagPillItem(t.Id.Value, t.Name))];

    private void UpdatePeople()
    {
        var people = BoardCardPerson.From(Model, _strings);
        if (people.SequenceEqual(People))
        {
            return;
        }

        People = people;
        OnPropertyChanged(nameof(People));
        OnPropertyChanged(nameof(HasPeople));
    }

    private void UpdateTags()
    {
        var tags = TagsOf(Model);
        if (tags.SequenceEqual(Tags))
        {
            return;
        }

        Tags = tags;
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTagStrip));
        OnPropertyChanged(nameof(TagsText));
    }

    private static string Format(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);
}
