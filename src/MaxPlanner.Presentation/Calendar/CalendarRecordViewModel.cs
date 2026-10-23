using System.Globalization;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Board;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Calendar;

/// <summary>
/// A CR row in Records mode and a CR card in the Backlog pane (B-08.3–4, design §4.4). It shows committed state
/// only; commands are run by <see cref="CalendarPageViewModel"/>. Use on the UI thread.
/// </summary>
public sealed class CalendarRecordViewModel : ProjectionItemViewModel<ChangeRequestReadModel, ChangeRequestId>
{
    private readonly ILocalizedStrings _strings;
    private readonly Func<ChangeRequestReadModel, PriorityLevel> _priorityLevel;

    public CalendarRecordViewModel(ChangeRequestReadModel model, ILocalizedStrings strings, Func<ChangeRequestReadModel, PriorityLevel> priorityLevel)
        : base(model)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _priorityLevel = priorityLevel ?? throw new ArgumentNullException(nameof(priorityLevel));
    }

    public override ChangeRequestId Key => Model.Id;

    public Guid Id => Model.Id.Value;

    public string Number => Model.Number;

    public string Title => Model.Title;

    public string OwnerName => Model.Owner.Name;

    public string SquadName => Model.SquadName;

    public string StatusName => Model.Status.Name;

    public string PriorityName => Model.Priority.Name;

    public PriorityLevel PriorityLevel => _priorityLevel(Model);

    /// <summary>"Priority: Urgent" (design §6.1).</summary>
    public string PriorityAccessibleName => _strings.Format("Board_PriorityName", Model.Priority.Name);

    public string PlannedStartText => Format(Model.PlannedStart);

    public string PlannedEndText => Format(Model.PlannedEnd);

    /// <summary>"Oct 13 – Oct 16", or "Unscheduled" (design §4.4 agenda and backlog).</summary>
    public string RangeText => Model.PlannedStart is { } start && Model.PlannedEnd is { } end
        ? start == end ? Short(start) : _strings.Format("Calendar_Range", Short(start), Short(end))
        : _strings.GetString("CrEditor_Unscheduled");

    public string LinkedTasksText => Model.LinkedTaskCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>"3 tasks" (design §4.4 backlog card); a link that opens the linked-task list.</summary>
    public string LinkedTasksLinkText => _strings.Format(Model.LinkedTaskCount == 1 ? "Calendar_LinkedTaskOne" : "Calendar_LinkedTasks", Model.LinkedTaskCount);

    public bool IsOverdue => Model.IsOverdue;

    /// <summary>"Overdue · 7 days" (design §4.4), from the B-15 predicate the Dashboard uses.</summary>
    public string OverdueText => Model.IsOverdue
        ? _strings.Format(Model.DaysOverdue == 1 ? "Calendar_OverdueOne" : "Calendar_Overdue", Model.DaysOverdue)
        : string.Empty;

    /// <summary>The compact (S) second line: "Owner · Status · ends Oct 16 · Overdue · 7 days".</summary>
    public string CompactLine => string.Join(" · ", new[] { OwnerName, StatusName, Model.PlannedEnd is { } end ? _strings.Format("Calendar_Ends", Short(end)) : RangeText, OverdueText }
        .Where(part => part.Length > 0));

    public string AccessibleName => _strings.Format("Calendar_RecordName", Number, Title, OwnerName, StatusName, RangeText) + (IsOverdue ? ", " + OverdueText : string.Empty);

    /// <summary>"More actions for CR-1042" (design §8.2).</summary>
    public string MenuName => _strings.Format("Checklists_ItemMenuName", Number);

    public string OpenLinkedTasksName => _strings.Format("Calendar_OpenLinkedTasksName", Number);

    public bool HasDates => Model.PlannedStart is not null;

    /// <summary>Move to backlog is offered while the CR has dates or a non-backlog status (B-08.11).</summary>
    public bool CanMoveToBacklog => HasDates || !Model.IsBacklog;

    protected override void OnModelUpdated(ChangeRequestReadModel previous) => OnPropertyChanged(string.Empty);

    private static string Format(DateOnly? date) => date is { } d ? d.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

    private static string Short(DateOnly date) => date.ToString("MMM d", CultureInfo.CurrentCulture);
}
