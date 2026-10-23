using System.Globalization;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Settings.DataPage;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings;

/// <summary>The services Settings needs beyond the workspace (B-11, T-09); optional parts are null in tests.</summary>
/// <param name="Theme">Applies and persists the appearance mode (AC-23).</param>
/// <param name="DataFolder">Shows and opens the portable data folder (T-03, T-09).</param>
/// <param name="Backups">Reads the newest backup for Settings › Data (T-09).</param>
/// <param name="Data">Import, export, backup, and restore (Data page, B-12, B-13, T-09); null disables them.</param>
public sealed record SettingsServices(SettingsUseCases UseCases, ThemeService? Theme, IDataFolderLauncher? DataFolder, IBackupInfoSource? Backups, DataServices? Data = null);

/// <summary>
/// The Settings page (B-04, B-11, T-09, design §4.6): appearance, workflow statuses with semantic flags,
/// priorities, squads, people and tags, defaults (including the default CR Owner), custom fields, and data
/// location. Every business change runs one Settings use case with a concurrency stamp (ADR 0022) and shows
/// only committed state afterwards (T-07). Use on the UI thread.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IPageViewModel
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly SettingsServices _services;
    private readonly IDisposable _workflowSubscription;
    private readonly IDisposable _commitSubscription;
    private WorkflowSnapshot? _workflow;
    private bool _isSyncing;
    private string _defaultsStamp = string.Empty;

    public SettingsPageViewModel(WorkspaceContext context, SettingsServices services)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        var strings = context.Strings;
        TaskStatuses = new LookupSectionViewModel(LookupKind.TaskStatus, "TaskStatuses", strings.GetString("Settings_TaskStatusesTitle"), "");
        CrStatuses = new LookupSectionViewModel(LookupKind.CrStatus, "CrStatuses", strings.GetString("Settings_CrStatusesTitle"), "");
        Priorities = new LookupSectionViewModel(LookupKind.Priority, "Priorities", strings.GetString("Settings_PrioritiesTitle"), "");
        Squads = new LookupSectionViewModel(LookupKind.Squad, "Squads", strings.GetString("Settings_SquadsTitle"), "");
        People = new LookupSectionViewModel(LookupKind.Person, "People", strings.GetString("Settings_PeopleTitle"), "");
        Tags = new LookupSectionViewModel(LookupKind.Tag, "Tags", strings.GetString("Settings_TagsTitle"), "");
        TaskFields = new FieldSectionViewModel(FieldTarget.Task, "TaskFields", strings.GetString("Settings_TaskFieldsTitle"));
        CrFields = new FieldSectionViewModel(FieldTarget.ChangeRequest, "CrFields", strings.GetString("Settings_CrFieldsTitle"));
        ThemeChoices = [strings.GetString("Settings_ThemeSystem"), strings.GetString("Settings_ThemeLight"), strings.GetString("Settings_ThemeDark")];
        ThemeIndex = (int)(services.Theme?.Mode ?? ThemePreference.System);
        DataFolderPath = services.DataFolder?.DataDirectory ?? string.Empty;
        _workflowSubscription = context.Store.Workflow.ObserveOn(context.UiScheduler).Subscribe(OnWorkflow);
        _commitSubscription = context.Store.Committed.ObserveOn(context.UiScheduler).Subscribe(_ => RefreshUsage());
        _ = RefreshBackupAsync();
    }

    public string Summary => _context.Strings.GetString("Settings_Summary");

    public IReadOnlyList<string> ThemeChoices { get; }

    /// <summary>0 System, 1 Light, 2 Dark (the <see cref="ThemePreference"/> order); applies immediately (AC-23).</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    public LookupSectionViewModel TaskStatuses { get; }

    public LookupSectionViewModel CrStatuses { get; }

    public LookupSectionViewModel Priorities { get; }

    public LookupSectionViewModel Squads { get; }

    public LookupSectionViewModel People { get; }

    public LookupSectionViewModel Tags { get; }

    public IReadOnlyList<LookupSectionViewModel> WorkflowSections => [TaskStatuses, CrStatuses, Priorities, Squads];

    public IReadOnlyList<LookupSectionViewModel> PeopleSections => [People, Tags];

    public FieldSectionViewModel TaskFields { get; }

    public FieldSectionViewModel CrFields { get; }

    public IReadOnlyList<FieldSectionViewModel> FieldSections => [TaskFields, CrFields];

    [ObservableProperty]
    public partial IReadOnlyList<SettingsChoice> TaskStatusChoices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SettingsChoice> CrStatusChoices { get; private set; } = [];

    /// <summary>CR statuses flagged Backlog: the only valid "Move to backlog" destinations (B-08.11).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingsChoice> CrBacklogChoices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SettingsChoice> PriorityChoices { get; private set; } = [];

    /// <summary>"None" followed by every person (B-04: no default CR Owner until one is chosen).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SettingsChoice> OwnerChoices { get; private set; } = [];

    [ObservableProperty]
    public partial int DefaultTaskStatusIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int DefaultCrStatusIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int DefaultPriorityIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int DefaultCrOwnerIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int SchedulingStatusIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int BacklogStatusIndex { get; set; } = -1;

    /// <summary>A page-level failure (settings changed elsewhere, storage); shown in the page InfoBar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    public string DataFolderPath { get; }

    public bool CanOpenDataFolder => _services.DataFolder is not null;

    [ObservableProperty]
    public partial string LastBackupText { get; private set; } = string.Empty;

    /// <summary>The Data page (D13), created on first use; null when the composition gave no data services.</summary>
    public DataPageViewModel? DataPage { get; private set; }

    public bool HasDataServices => _services.Data is not null;

    /// <summary>The Data page replaces the Settings cards inside the Settings destination (breadcrumb "Settings › Data").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardsVisible))]
    public partial bool IsDataPageOpen { get; private set; }

    public bool IsCardsVisible => !IsDataPageOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand))]
    public partial bool IsBackingUp { get; private set; }

    public void Dispose()
    {
        _workflowSubscription.Dispose();
        _commitSubscription.Dispose();
        if (DataPage is { } page)
        {
            page.CloseRequested -= OnDataPageCloseRequested;
            page.Backups.BackupsChanged -= OnBackupsChanged;
            page.Dispose();
        }
    }

    /// <summary>Opens Settings › Data on a workflow; the Grid's Export… passes its current view (B-12).</summary>
    public void OpenDataPage(DataMode mode, ViewExportScope? view = null)
    {
        if (EnsureDataPage() is { } page)
        {
            page.Show(mode, view);
            IsDataPageOpen = true;
        }
    }

    private DataPageViewModel? EnsureDataPage()
    {
        if (DataPage is null && _services.Data is { } data)
        {
            DataPage = new DataPageViewModel(_context, data);
            DataPage.CloseRequested += OnDataPageCloseRequested;
            DataPage.Backups.BackupsChanged += OnBackupsChanged;
            OnPropertyChanged(nameof(DataPage));
        }

        return DataPage;
    }

    private void OnDataPageCloseRequested(object? sender, EventArgs e) => IsDataPageOpen = false;

    private void OnBackupsChanged(object? sender, EventArgs e) => _ = RefreshBackupAsync();

    [RelayCommand]
    private void OpenData() => OpenDataPage(DataMode.Import);

    [RelayCommand]
    private void ManageBackups() => OpenDataPage(DataMode.Backups);

    private bool CanBackUpNow => HasDataServices && !IsBackingUp;

    /// <summary>T-09 Back up now: a manual backup, never pruned.</summary>
    [RelayCommand(CanExecute = nameof(CanBackUpNow))]
    private async Task BackUpNowAsync()
    {
        if (_services.Data is not { } data)
        {
            return;
        }

        IsBackingUp = true;
        try
        {
            var result = await data.Maintenance.CreateBackupAsync("manual", CancellationToken.None).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _context.Announce(_context.Strings.GetString("Backups_Created"));
            }
            else
            {
                ErrorMessage = CommandErrorText.Describe(result.Error!, "Record_Backup", _context.Strings);
            }

            await RefreshBackupAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    // ----- Lookups -----

    /// <summary>"+ Add status" (design §4.6): appends a value; names must be unique (inline error).</summary>
    public async Task AddAsync(LookupSectionViewModel section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.IsAdding)
        {
            return;
        }

        section.AddError = null;
        if (string.IsNullOrWhiteSpace(section.AddText))
        {
            section.AddError = _context.Strings.Format("Error_Required", _context.Strings.GetString("Field_Name"));
            return;
        }

        section.IsAdding = true;
        var id = Guid.NewGuid();
        var error = await RunAsync(op => _services.UseCases.AddLookup.ExecuteAsync(new AddLookupCommand(op, section.Kind, id, section.AddText), CancellationToken.None)).ConfigureAwait(true);
        section.IsAdding = false;
        if (error is null)
        {
            section.AddText = string.Empty;
        }
        else
        {
            section.AddError = error;
        }
    }

    /// <returns>True when the rename was saved (or unchanged) and the editor closed.</returns>
    public async Task<bool> CommitRenameAsync(LookupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsBusy || !row.IsRenaming)
        {
            return false;
        }

        if (string.Equals(row.RenameText.Trim(), row.Name, StringComparison.Ordinal))
        {
            row.IsRenaming = false;
            return true;
        }

        row.IsBusy = true;
        var error = await RunAsync(op => _services.UseCases.RenameLookup.ExecuteAsync(
            new RenameLookupCommand(op, row.Kind, row.Id, row.Stamp, row.RenameText), CancellationToken.None)).ConfigureAwait(true);
        row.IsBusy = false;
        if (error is not null)
        {
            row.RenameError = error;
            return false;
        }

        row.IsRenaming = false;
        row.RenameError = null;
        return true;
    }

    public Task MoveAsync(LookupRowViewModel row, int offset)
    {
        ArgumentNullException.ThrowIfNull(row);
        var section = SectionOf(row.Kind);
        return RunRowAsync(row, op => _services.UseCases.MoveLookup.ExecuteAsync(new MoveLookupCommand(op, row.Kind, row.Id, section.OrderStamp, offset), CancellationToken.None));
    }

    /// <summary>Changes one semantic flag of a status (B-04). Completion and date automation follow flags, never labels.</summary>
    public Task SetFlagAsync(LookupRowViewModel row, StatusFlagKind flag, bool value)
    {
        ArgumentNullException.ThrowIfNull(row);
        var completed = flag == StatusFlagKind.Completed ? value : row.IsCompleted;
        var setsStart = flag == StatusFlagKind.SetsStartDate ? value : row.SetsStartDate;
        var backlog = flag == StatusFlagKind.Backlog ? value : row.IsBacklog;
        return row.Kind switch
        {
            LookupKind.TaskStatus => RunRowAsync(row, op => _services.UseCases.SetTaskStatusFlags.ExecuteAsync(
                new SetTaskStatusFlagsCommand(op, new TaskStatusId(row.Id), row.Stamp, completed, setsStart, backlog), CancellationToken.None)),
            LookupKind.CrStatus => RunRowAsync(row, op => _services.UseCases.SetCrStatusFlags.ExecuteAsync(
                new SetCrStatusFlagsCommand(op, new CrStatusId(row.Id), row.Stamp, completed, backlog), CancellationToken.None)),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>Row ⋯ › Set as default: the default for new tasks, new CRs, or both priorities (B-04).</summary>
    public Task SetAsDefaultAsync(LookupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_workflow is not { } workflow)
        {
            return Task.CompletedTask;
        }

        var d = workflow.Defaults;
        d = row.Kind switch
        {
            LookupKind.TaskStatus => d with { DefaultTaskStatusId = new TaskStatusId(row.Id) },
            LookupKind.CrStatus => d with { DefaultCrStatusId = new CrStatusId(row.Id) },
            LookupKind.Priority => d with { DefaultPriorityId = new PriorityId(row.Id) },
            LookupKind.Person => d with { DefaultCrOwnerId = new PersonId(row.Id) },
            _ => d,
        };
        return SaveDefaultsAsync(d);
    }

    /// <summary>
    /// Remove… (design §5.9): reads the current usage, then opens the replacement dialog, which runs the removal
    /// itself so a failure keeps the dialog and its choice.
    /// </summary>
    public async Task RemoveAsync(LookupRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_workflow is not { } workflow || _context.IsEditingBlocked)
        {
            ErrorMessage = _context.IsEditingBlocked ? _context.Strings.GetString("Error_EditingBlocked") : null;
            return;
        }

        var removal = new LookupRemovalViewModel(_context, _services.UseCases, workflow, row.Kind, row.Id, row.Name);
        await removal.LoadAsync().ConfigureAwait(true);
        await _context.ShowDialogAsync(removal).ConfigureAwait(true);
    }

    // ----- Defaults -----

    partial void OnThemeIndexChanged(int value)
    {
        if (value is >= 0 and <= 2 && _services.Theme is { } theme && (int)theme.Mode != value)
        {
            theme.Select((ThemePreference)value);
        }
    }

    partial void OnDefaultTaskStatusIndexChanged(int value) => OnDefaultChanged(value);

    partial void OnDefaultCrStatusIndexChanged(int value) => OnDefaultChanged(value);

    partial void OnDefaultPriorityIndexChanged(int value) => OnDefaultChanged(value);

    partial void OnDefaultCrOwnerIndexChanged(int value) => OnDefaultChanged(value);

    partial void OnSchedulingStatusIndexChanged(int value) => OnDefaultChanged(value);

    partial void OnBacklogStatusIndexChanged(int value) => OnDefaultChanged(value);

    // ----- Custom fields -----

    /// <summary>Add field… (design §4.6): the field dialog creates the definition.</summary>
    public Task AddFieldAsync(FieldSectionViewModel section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return _workflow is { } workflow
            ? _context.ShowDialogAsync(FieldDefinitionEditorViewModel.ForNew(_context, _services.UseCases, workflow, section.Target))
            : Task.CompletedTask;
    }

    public Task EditFieldAsync(FieldRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _workflow is { } workflow && workflow.Fields.FirstOrDefault(f => f.Id == row.Id) is { } field
            ? _context.ShowDialogAsync(FieldDefinitionEditorViewModel.ForExisting(_context, _services.UseCases, workflow, field))
            : Task.CompletedTask;
    }

    public Task MoveFieldAsync(FieldRowViewModel row, int offset)
    {
        ArgumentNullException.ThrowIfNull(row);
        var section = _workflow?.Fields.FirstOrDefault(f => f.Id == row.Id)?.Target == FieldTarget.ChangeRequest ? CrFields : TaskFields;
        return RunPageAsync(op => _services.UseCases.MoveFieldDefinition.ExecuteAsync(new MoveFieldDefinitionCommand(op, row.Id, section.OrderStamp, offset), CancellationToken.None));
    }

    /// <summary>Remove… on a field deactivates it after a confirmation; its stored values stay in exports and backups (B-11).</summary>
    public async Task DeactivateFieldAsync(FieldRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var strings = _context.Strings;
        var confirmation = new Common.Dialogs.ConfirmationViewModel(
            "Settings_RemoveFieldDialog",
            strings.Format("Settings_RemoveFieldTitle", row.Label),
            strings.GetString("Settings_RemoveFieldMessage"),
            strings.GetString("Settings_RemoveFieldButton"),
            null,
            strings.GetString("Common_Cancel"));
        await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
        if (confirmation.Result == Common.Dialogs.ConfirmationResult.Primary)
        {
            await RunPageAsync(op => _services.UseCases.DeactivateFieldDefinition.ExecuteAsync(
                new DeactivateFieldDefinitionCommand(op, row.Id, row.Stamp), CancellationToken.None)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _services.DataFolder?.OpenDataFolder();

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private LookupSectionViewModel SectionOf(LookupKind kind) => kind switch
    {
        LookupKind.TaskStatus => TaskStatuses,
        LookupKind.CrStatus => CrStatuses,
        LookupKind.Priority => Priorities,
        LookupKind.Squad => Squads,
        LookupKind.Person => People,
        _ => Tags,
    };

    private void OnDefaultChanged(int value)
    {
        if (_isSyncing || value < 0 || _workflow is not { } workflow)
        {
            return;
        }

        Guid? Pick(IReadOnlyList<SettingsChoice> choices, int index) => index >= 0 && index < choices.Count ? choices[index].Id : null;
        var d = workflow.Defaults;
        var defaults = d with
        {
            DefaultTaskStatusId = Pick(TaskStatusChoices, DefaultTaskStatusIndex) is { } task ? new TaskStatusId(task) : d.DefaultTaskStatusId,
            DefaultCrStatusId = Pick(CrStatusChoices, DefaultCrStatusIndex) is { } cr ? new CrStatusId(cr) : d.DefaultCrStatusId,
            DefaultPriorityId = Pick(PriorityChoices, DefaultPriorityIndex) is { } priority ? new PriorityId(priority) : d.DefaultPriorityId,
            DefaultCrOwnerId = DefaultCrOwnerIndex >= 0 ? (Pick(OwnerChoices, DefaultCrOwnerIndex) is { } owner ? new PersonId(owner) : null) : d.DefaultCrOwnerId,
            CalendarSchedulingStatusId = Pick(CrStatusChoices, SchedulingStatusIndex) is { } scheduling ? new CrStatusId(scheduling) : d.CalendarSchedulingStatusId,
            CrBacklogStatusId = Pick(CrBacklogChoices, BacklogStatusIndex) is { } backlog ? new CrStatusId(backlog) : d.CrBacklogStatusId,
        };
        if (defaults != d)
        {
            _ = SaveDefaultsAsync(defaults);
        }
    }

    private async Task SaveDefaultsAsync(WorkflowDefaults defaults)
    {
        var error = await RunAsync(op => _services.UseCases.UpdateWorkflowDefaults.ExecuteAsync(
            new UpdateWorkflowDefaultsCommand(
                op,
                _defaultsStamp,
                defaults.DefaultTaskStatusId,
                defaults.DefaultCrStatusId,
                defaults.DefaultPriorityId,
                defaults.DefaultCrOwnerId,
                defaults.CalendarSchedulingStatusId,
                defaults.CrBacklogStatusId),
            CancellationToken.None)).ConfigureAwait(true);
        if (error is not null)
        {
            ErrorMessage = error;

            // The combo boxes show the committed defaults again (T-07: committed state only).
            if (_workflow is { } workflow)
            {
                SyncDefaults(workflow);
            }
        }
    }

    private async Task RunRowAsync(LookupRowViewModel row, Func<OperationId, Task<AppResult<CommandOutcome<long>>>> command)
    {
        if (row.IsBusy)
        {
            return;
        }

        row.IsBusy = true;
        var error = await RunAsync(command).ConfigureAwait(true);
        row.IsBusy = false;
        ErrorMessage = error;
    }

    private async Task RunPageAsync(Func<OperationId, Task<AppResult<CommandOutcome<long>>>> command) =>
        ErrorMessage = await RunAsync(command).ConfigureAwait(true);

    /// <summary>Runs one Settings command with the save indicator; returns the failure text or null on success.</summary>
    private async Task<string?> RunAsync(Func<OperationId, Task<AppResult<CommandOutcome<long>>>> command)
    {
        if (_context.IsEditingBlocked)
        {
            return _context.Strings.GetString("Error_EditingBlocked");
        }

        var operation = OperationId.New();
        _context.SaveStatus.Begin();
        var result = await command(operation).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Settings", _context.Strings);
            _context.SaveStatus.Report(failure);
            _context.Announce(failure.Message, isFailure: true);
            return failure.Message;
        }

        _context.SaveStatus.Complete();
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        return null;
    }

    private void OnWorkflow(WorkflowSnapshot workflow)
    {
        _workflow = workflow;
        foreach (var section in WorkflowSections.Concat(PeopleSections))
        {
            SyncSection(section, workflow);
        }

        SyncFields(TaskFields, workflow);
        SyncFields(CrFields, workflow);
        SyncDefaults(workflow);
        RefreshUsage();
    }

    private void SyncSection(LookupSectionViewModel section, WorkflowSnapshot workflow)
    {
        var entries = WorkflowLookups.Entries(workflow, section.Kind);
        section.OrderStamp = WorkflowStamps.Order(workflow, section.Kind);
        KeyedCollectionSync.Sync(
            section.MutableRows,
            entries,
            e => e.Id,
            r => r.Id,
            e => Update(new LookupRowViewModel(section.Kind, e.Id, section.AutomationKey), e, entries, workflow),
            (row, e) => Update(row, e, entries, workflow));
        var countKey = section.Kind switch
        {
            LookupKind.TaskStatus or LookupKind.CrStatus => "Settings_StatusesDescription",
            LookupKind.Priority => "Settings_PrioritiesDescription",
            LookupKind.Squad => "Settings_SquadsDescription",
            LookupKind.Person => "Settings_PeopleDescription",
            _ => "Settings_TagsDescription",
        };
        section.Description = _context.Strings.Format(countKey, entries.Count);
    }

    private LookupRowViewModel Update(LookupRowViewModel row, LookupEntry entry, IReadOnlyList<LookupEntry> entries, WorkflowSnapshot workflow)
    {
        var strings = _context.Strings;
        var d = workflow.Defaults;
        var flags = new List<string>();
        row.Name = entry.Name.Display;
        row.Stamp = WorkflowStamps.Item(workflow, entry.Kind, entry.Id);
        var index = entries.ToList().FindIndex(e => e.Id == entry.Id);
        row.CanMoveUp = WorkflowLookups.IsOrdered(entry.Kind) && index > 0;
        row.CanMoveDown = WorkflowLookups.IsOrdered(entry.Kind) && index < entries.Count - 1;
        switch (entry.Kind)
        {
            case LookupKind.TaskStatus when workflow.FindTaskStatus(new TaskStatusId(entry.Id)) is { } status:
                row.IsDefault = d.DefaultTaskStatusId == status.Id;
                (row.IsCompleted, row.SetsStartDate, row.IsBacklog) = (status.IsCompleted, status.SetsStartDate, status.IsBacklog);
                AddIf(flags, row.IsDefault, "Settings_FlagDefault");
                AddIf(flags, status.IsBacklog, "Settings_FlagBacklog");
                AddIf(flags, status.SetsStartDate, "Settings_FlagSetsStartDate");
                AddIf(flags, status.IsCompleted, "Settings_FlagCompletedTask");
                break;
            case LookupKind.CrStatus when workflow.FindCrStatus(new CrStatusId(entry.Id)) is { } status:
                row.IsDefault = d.DefaultCrStatusId == status.Id;
                (row.IsCompleted, row.SetsStartDate, row.IsBacklog) = (status.IsCompleted, false, status.IsBacklog);
                AddIf(flags, row.IsDefault, "Settings_FlagDefault");
                AddIf(flags, status.IsBacklog, "Settings_FlagBacklog");
                AddIf(flags, status.IsCompleted, "Settings_FlagCompleted");
                AddIf(flags, d.CalendarSchedulingStatusId == status.Id, "Settings_FlagScheduling");
                AddIf(flags, d.CrBacklogStatusId == status.Id, "Settings_FlagBacklogDestination");
                break;
            case LookupKind.Priority:
                row.IsDefault = d.DefaultPriorityId.Value == entry.Id;
                AddIf(flags, row.IsDefault, "Settings_FlagDefault");
                break;
            case LookupKind.Person:
                row.IsDefault = d.DefaultCrOwnerId?.Value == entry.Id;
                AddIf(flags, row.IsDefault, "Settings_FlagDefaultOwner");
                break;
            default:
                row.IsDefault = false;
                break;
        }

        row.Flags = flags;
        row.MenuName = strings.Format("Settings_RowMenuName", row.Name);
        UpdateAccessibleName(row);
        return row;

        void AddIf(List<string> list, bool condition, string key)
        {
            if (condition)
            {
                list.Add(strings.GetString(key));
            }
        }
    }

    private static void UpdateAccessibleName(LookupRowViewModel row) =>
        row.AccessibleName = string.Join(", ", new[] { row.Name }.Concat(row.Flags).Append(row.UsageText).Where(s => s.Length > 0));

    private void SyncFields(FieldSectionViewModel section, WorkflowSnapshot workflow)
    {
        var strings = _context.Strings;
        var fields = workflow.FieldsFor(section.Target).Where(f => f.IsActive).OrderBy(f => f.DisplayOrder).ThenBy(f => f.Id.Value).ToList();
        section.OrderStamp = WorkflowStamps.FieldOrder(workflow, section.Target);
        KeyedCollectionSync.Sync(
            section.MutableRows,
            fields,
            f => f.Id,
            r => r.Id,
            f => UpdateField(new FieldRowViewModel(f.Id, section.AutomationKey), f),
            (row, f) => UpdateField(row, f));
        section.Description = strings.Format("Settings_FieldsDescription", fields.Count);

        FieldRowViewModel UpdateField(FieldRowViewModel row, FieldDefinition field)
        {
            var index = fields.FindIndex(f => f.Id == field.Id);
            row.Label = field.Label.Display;
            row.Details = strings.Format("Settings_FieldDetails", strings.GetString("FieldKind_" + field.Kind), field.Key.Display);
            var flags = new List<string>();
            if (field.IsRequired)
            {
                flags.Add(strings.GetString("Settings_FlagRequired"));
            }

            if (field.DefaultValue is not null)
            {
                flags.Add(strings.GetString("Settings_FlagHasDefault"));
            }

            row.Flags = flags;
            row.Stamp = WorkflowStamps.Field(workflow, field.Id);
            row.CanMoveUp = index > 0;
            row.CanMoveDown = index < fields.Count - 1;
            row.MenuName = strings.Format("Settings_RowMenuName", row.Label);
            row.AccessibleName = string.Join(", ", new[] { row.Label, row.Details }.Concat(flags));
            return row;
        }
    }

    private void SyncDefaults(WorkflowSnapshot workflow)
    {
        _isSyncing = true;
        try
        {
            var d = workflow.Defaults;
            _defaultsStamp = WorkflowStamps.Defaults(workflow);
            TaskStatusChoices = [.. workflow.TaskStatuses.OrderBy(s => s.Order).Select(s => new SettingsChoice(s.Id.Value, s.Name.Display))];
            CrStatusChoices = [.. workflow.CrStatuses.OrderBy(s => s.Order).Select(s => new SettingsChoice(s.Id.Value, s.Name.Display))];
            CrBacklogChoices = [.. workflow.CrStatuses.Where(s => s.IsBacklog).OrderBy(s => s.Order).Select(s => new SettingsChoice(s.Id.Value, s.Name.Display))];
            PriorityChoices = [.. workflow.Priorities.OrderBy(p => p.Order).Select(p => new SettingsChoice(p.Id.Value, p.Name.Display))];
            OwnerChoices = [new SettingsChoice(null, _context.Strings.GetString("Settings_NoDefaultOwner")), .. workflow.People.Select(p => new SettingsChoice(p.Id.Value, p.Name.Display))];
            DefaultTaskStatusIndex = IndexOf(TaskStatusChoices, d.DefaultTaskStatusId.Value);
            DefaultCrStatusIndex = IndexOf(CrStatusChoices, d.DefaultCrStatusId.Value);
            DefaultPriorityIndex = IndexOf(PriorityChoices, d.DefaultPriorityId.Value);
            DefaultCrOwnerIndex = IndexOf(OwnerChoices, d.DefaultCrOwnerId?.Value);
            SchedulingStatusIndex = IndexOf(CrStatusChoices, d.CalendarSchedulingStatusId.Value);
            BacklogStatusIndex = IndexOf(CrBacklogChoices, d.CrBacklogStatusId.Value);
        }
        finally
        {
            _isSyncing = false;
        }

        static int IndexOf(IReadOnlyList<SettingsChoice> choices, Guid? id)
        {
            for (var i = 0; i < choices.Count; i++)
            {
                if (choices[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Row usage counts ("12 tasks · 3 Subtask cards") from the committed projections, archived tasks included.
    /// Display only: the removal dialog reads authoritative counts from the database (design §5.9).
    /// </summary>
    private void RefreshUsage()
    {
        if (_workflow is null)
        {
            return;
        }

        var store = _context.Store;
        var tasks = store.Tasks.Items.ToList();
        var cards = store.SubtaskCards.Items.ToList();
        var crs = store.ChangeRequests.Items.ToList();
        var reviews = store.ReviewItems.Items.ToList();
        var strings = _context.Strings;

        string Text(int taskCount, int cardCount, int crCount, int ownerCount, int reviewCount)
        {
            var parts = new List<string>();
            void Add(int count, string key)
            {
                if (count > 0)
                {
                    parts.Add(strings.Format(key, count));
                }
            }

            Add(taskCount, "Settings_UsageTasks");
            Add(cardCount, "Settings_UsageSubtaskCards");
            Add(crCount, "Settings_UsageCrs");
            Add(ownerCount, "Settings_UsageCrOwner");
            Add(reviewCount, "Settings_UsageReviews");
            return parts.Count == 0 ? strings.GetString("Settings_UsageNone") : string.Join(" · ", parts);
        }

        foreach (var row in TaskStatuses.Rows)
        {
            row.UsageText = Text(tasks.Count(t => t.Status.Id.Value == row.Id), cards.Count(c => c.Status.Id.Value == row.Id), 0, 0, 0);
            UpdateAccessibleName(row);
        }

        foreach (var row in CrStatuses.Rows)
        {
            row.UsageText = Text(0, 0, crs.Count(c => c.Status.Id.Value == row.Id), 0, 0);
            UpdateAccessibleName(row);
        }

        foreach (var row in Priorities.Rows)
        {
            row.UsageText = Text(tasks.Count(t => t.Priority.Id.Value == row.Id), 0, crs.Count(c => c.Priority.Id.Value == row.Id), 0, 0);
            UpdateAccessibleName(row);
        }

        foreach (var row in Squads.Rows)
        {
            row.UsageText = Text(0, 0, crs.Count(c => c.SquadId?.Value == row.Id), 0, 0);
            UpdateAccessibleName(row);
        }

        foreach (var row in People.Rows)
        {
            var id = new PersonId(row.Id);
            row.UsageText = Text(
                tasks.Count(t => t.Designers.Any(p => p.Id == id) || t.Developers.Any(p => p.Id == id)),
                0,
                0,
                crs.Count(c => c.Owner.Id == id),
                reviews.Count(r => r.Content.DeveloperId == id));
            UpdateAccessibleName(row);
        }

        foreach (var row in Tags.Rows)
        {
            var id = new TagId(row.Id);
            row.UsageText = Text(tasks.Count(t => t.Tags.Any(g => g.Id == id)), 0, 0, 0, 0);
            UpdateAccessibleName(row);
        }
    }

    private async Task RefreshBackupAsync()
    {
        if (_services.Backups is not { } backups)
        {
            LastBackupText = _context.Strings.GetString("Settings_BackupUnknown");
            return;
        }

        try
        {
            var latest = await backups.GetLatestAsync(CancellationToken.None).ConfigureAwait(true);
            LastBackupText = latest is null
                ? _context.Strings.GetString("Settings_BackupNone")
                : _context.Strings.Format(
                    "Settings_BackupLast",
                    latest.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    _context.Strings.GetString("BackupKind_" + latest.Kind.Replace("-", string.Empty, StringComparison.Ordinal)));
        }
        catch (IOException)
        {
            LastBackupText = _context.Strings.GetString("Settings_BackupUnknown");
        }
        catch (UnauthorizedAccessException)
        {
            LastBackupText = _context.Strings.GetString("Settings_BackupUnknown");
        }
    }
}

/// <summary>A semantic status flag (B-04) changed from a row menu.</summary>
public enum StatusFlagKind
{
    Completed,
    SetsStartDate,
    Backlog,
}
