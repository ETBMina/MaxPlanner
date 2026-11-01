using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>
/// Import (B-12, B-13, design §4.6): Source → Map (external) → Lookups and relationships → Preview → Result.
/// The preview writes nothing; Apply takes a safety backup of a non-empty dataset, then commits the reviewed plan in one
/// transaction (T-10). A Max Planner export skips the mapping step because it carries its own IDs and definitions; it
/// visits the Lookups step only when a configuration replace removes values that are in use (ADR 0026).
/// </summary>
public sealed partial class ImportWizardViewModel : WizardViewModel
{
    private const int SourceStep = 0;
    private const int MapStep = 1;
    private const int LookupsStep = 2;
    private const int OwnerStep = 3;
    private const int PreviewStep = 4;
    private const int ResultStep = 5;
    private const int MaxFindingRows = 1_000;

    private static readonly IReadOnlyList<ExternalEntity> Entities = [ExternalEntity.Task, ExternalEntity.ChangeRequest, ExternalEntity.Review, ExternalEntity.GeneralTodo, ExternalEntity.Ignore];

    private readonly WorkspaceContext _context;
    private readonly DataServices _services;
    private readonly List<string> _paths = [];
    private string? _generalChecklistPath;
    private ExternalSource? _source;
    private ExternalImportDraft? _draft;
    private TransferDocument? _document;
    private ImportPlan? _plan;
    private OperationId _operationId = OperationId.New();

    public ImportWizardViewModel(WorkspaceContext context, DataServices services)
        : base(context.Strings, ["Import_Step_Source", "Import_Step_Map", "Import_Step_Lookups", "Import_Step_Owner", "Import_Step_Preview", "Import_Step_Result"])
    {
        _context = context;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        EntityChoices = [.. Entities.Select(e => Strings.GetString("Import_Entity_" + e))];
        ModeChoices = [Strings.GetString("Import_Mode_CreateOnly"), Strings.GetString("Import_Mode_Merge")];
        ContentChoices = [.. ExportWizardViewModel.Contents.Select(c => Strings.GetString("Transfer_Content_" + c))];
    }

    public IReadOnlyList<string> EntityChoices { get; }

    public IReadOnlyList<string> ModeChoices { get; }

    public IReadOnlyList<string> ContentChoices { get; }

    /// <summary>ADR 0026: Both (default) / Configuration only / Data only, for Max Planner files with definitions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentHelp))]
    public partial int ContentIndex { get; set; }

    /// <summary>A single CSV table carries no definitions, so it keeps its data import without a content choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentAvailable), nameof(ContentHelp))]
    public partial bool IsSingleCsv { get; private set; }

    public bool IsContentAvailable => !IsSingleCsv;

    public string ContentHelp => IsSingleCsv ? Strings.GetString("Import_ContentSingleCsv") : Strings.GetString("Import_ContentHelp_" + Content);

    /// <summary>ADR 0026: in-use values a configuration replace removes, each with its replacement choice.</summary>
    public ObservableCollection<ImportRemovalViewModel> RemovalChoices { get; } = [];

    [ObservableProperty]
    public partial bool HasRemovals { get; private set; }

    /// <summary>The previewed plan replaces the configuration, so Apply needs <see cref="IsReplaceConfirmed"/>.</summary>
    [ObservableProperty]
    public partial bool IsConfigurationReplace { get; private set; }

    /// <summary>"I understand the current configuration will be replaced" (design §4.6).</summary>
    [ObservableProperty]
    public partial bool IsReplaceConfirmed { get; set; }

    private TransferContent Content => ExportWizardViewModel.Contents[Math.Clamp(ContentIndex, 0, ExportWizardViewModel.Contents.Count - 1)];

    /// <summary>False: a Max Planner export (JSON, XLSX, CSV); true: external data (CSV, XLSX, JSON, Markdown) with mapping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnFormat))]
    public partial bool IsExternal { get; set; }

    public bool IsOwnFormat => !IsExternal;

    /// <summary>0 Create only (default, T-10), 1 Merge (explicit).</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    /// <summary>B-13.5: the source namespace that keys the import ledger.</summary>
    [ObservableProperty]
    public partial string SourceNamespace { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GeneralChecklistText { get; private set; } = string.Empty;

    public ObservableCollection<ImportTableViewModel> Tables { get; } = [];

    public ObservableCollection<ImportChoiceViewModel> ValueChoices { get; } = [];

    public ObservableCollection<ImportChoiceViewModel> CrConflicts { get; } = [];

    public ObservableCollection<ImportChoiceViewModel> CrReferenceChoices { get; } = [];

    /// <summary>B-13.7: existing people, then the people this import creates (after people are mapped).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> OwnerChoices { get; private set; } = [];

    /// <summary>B-13.7: the owner for new CRs without one (required); the default CR Owner is proposed. -1 = not chosen.</summary>
    [ObservableProperty]
    public partial int FallbackOwnerIndex { get; set; } = -1;

    /// <summary>"N imported CRs have no owner…" on the Owner step.</summary>
    [ObservableProperty]
    public partial string MissingOwnerText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNoDecisions { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ImportCountRow> CountRows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ImportFindingRow> Findings { get; private set; } = [];

    [ObservableProperty]
    public partial string PreviewSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PresenceText { get; private set; } = string.Empty;

    /// <summary>ADR 0027: how many unmapped columns of the imported tables are kept hidden and how many are discarded.</summary>
    [ObservableProperty]
    public partial string UnmappedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    public override string NextText => Strings.GetString(StepIndex == PreviewStep ? "Import_ApplyButtonText" : "Data_NextButtonText");

    protected override bool CanAdvance => StepIndex switch
    {
        SourceStep => _paths.Count > 0 && (!IsExternal || !string.IsNullOrWhiteSpace(SourceNamespace)),
        LookupsStep when !IsExternal => RemovalChoices.All(c => c.SelectedIndex >= 0),
        OwnerStep => FallbackOwnerIndex >= 0 && FallbackOwnerIndex < _ownerOptions.Count,
        PreviewStep => _plan?.CanApply == true && !_context.IsEditingBlocked && (!IsConfigurationReplace || IsReplaceConfirmed),
        _ => true,
    };

    // Parallel to OwnerChoices: an existing person's ID, or the name of a person the import creates.
    private List<(Guid? Id, string? Name)> _ownerOptions = [];

    private bool HasMissingOwners => _draft is { MissingOwnerCount: > 0 };

    partial void OnFallbackOwnerIndexChanged(int value) => RefreshCommands();

    partial void OnIsExternalChanged(bool value)
    {
        _paths.Clear();
        _generalChecklistPath = null;
        SourceText = string.Empty;
        GeneralChecklistText = string.Empty;
        IsSingleCsv = false;
        ClearRemovals();
        RefreshCommands();
    }

    partial void OnSourceNamespaceChanged(string value) => RefreshCommands();

    partial void OnIsReplaceConfirmedChanged(bool value) => RefreshCommands();

    // A Max Planner file skips the Map step, and visits the Lookups step only for in-use removals.
    protected override int PreviousStep(int current) => (current, IsExternal) switch
    {
        (PreviewStep, false) => HasRemovals ? LookupsStep : SourceStep,
        (LookupsStep, false) => SourceStep,
        (PreviewStep, true) => HasMissingOwners ? OwnerStep : LookupsStep,
        _ => current - 1,
    };

    protected override async Task<int?> AdvanceAsync(CancellationToken cancellationToken)
    {
        switch (StepIndex)
        {
            case SourceStep:
                return IsExternal ? await ReadExternalAsync(cancellationToken).ConfigureAwait(true) : await ReadOwnAsync(cancellationToken).ConfigureAwait(true);
            case MapStep:
                return await BuildDraftAsync(cancellationToken).ConfigureAwait(true) ? LookupsStep : null;
            case LookupsStep when !IsExternal:
                return _document is { } document && await PreviewAsync(document, cancellationToken).ConfigureAwait(true) ? PreviewStep : null;
            case LookupsStep:
                if (!await BuildDraftAsync(cancellationToken).ConfigureAwait(true))
                {
                    return null;
                }

                // B-13.7: CRs without an owner ask for one after people are mapped, from existing and new people.
                if (HasMissingOwners)
                {
                    SyncOwnerChoices(_draft!);
                    return OwnerStep;
                }

                return await PreviewAsync(_draft!.Document, cancellationToken).ConfigureAwait(true) ? PreviewStep : null;
            case OwnerStep:
                return await BuildDraftAsync(cancellationToken).ConfigureAwait(true) && await PreviewAsync(_draft!.Document, cancellationToken).ConfigureAwait(true) ? PreviewStep : null;
            case PreviewStep:
                return await ApplyAsync(cancellationToken).ConfigureAwait(true) ? ResultStep : null;
            default:
                return null;
        }
    }

    // ----- Source -----
    [RelayCommand]
    private async Task PickFilesAsync()
    {
        IReadOnlyList<string> extensions = IsExternal ? [".csv", ".xlsx", ".json", ".md"] : [".json", ".xlsx", ".csv", ".zip"];
        var files = await _services.Pickers.PickOpenFilesAsync(extensions, allowMultiple: IsExternal).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        _paths.Clear();
        _paths.AddRange(files);
        IsSingleCsv = !IsExternal && files[0].EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        ClearRemovals();
        SourceText = string.Join(Environment.NewLine, files.Select(Path.GetFileName));
        ProposeNamespace(Path.GetFileNameWithoutExtension(files[0]));
        RefreshCommands();
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (await _services.Pickers.PickFolderAsync().ConfigureAwait(true) is not { } folder)
        {
            return;
        }

        _paths.Clear();
        _paths.Add(folder);
        SourceText = Strings.Format("Import_MarkdownFolder", folder);
        ProposeNamespace(Path.GetFileName(folder));
        RefreshCommands();
    }

    [RelayCommand]
    private async Task PickGeneralChecklistAsync()
    {
        var files = await _services.Pickers.PickOpenFilesAsync([".md"], allowMultiple: false).ConfigureAwait(true);
        _generalChecklistPath = files.Count > 0 ? files[0] : null;
        GeneralChecklistText = _generalChecklistPath is null ? string.Empty : Path.GetFileName(_generalChecklistPath);
    }

    private void ProposeNamespace(string name)
    {
        if (string.IsNullOrWhiteSpace(SourceNamespace))
        {
            SourceNamespace = name;
        }
    }

    private async Task<int?> ReadOwnAsync(CancellationToken cancellationToken)
    {
        var document = await _services.Reader.ReadAsync(_paths[0], null, cancellationToken).ConfigureAwait(true);
        if (!document.IsSuccess)
        {
            ErrorMessage = Describe(document.Error!);
            return null;
        }

        _draft = null;
        _document = document.Value;

        // ADR 0026: a view or data-only export holds only part of a configuration, so it imports as data.
        if (!IsSingleCsv && !ImportPlanner.HasCompleteConfiguration(document.Value))
        {
            ContentIndex = ExportWizardViewModel.Contents.ToList().IndexOf(TransferContent.DataOnly);
        }

        if (!await PreviewAsync(document.Value, cancellationToken).ConfigureAwait(true))
        {
            return null;
        }

        // ADR 0026: in-use values the replace removes need a replacement before the preview can apply.
        return HasRemovals ? LookupsStep : PreviewStep;
    }

    private async Task<int?> ReadExternalAsync(CancellationToken cancellationToken)
    {
        var source = await _services.ExternalReader.ReadAsync([.. _paths], _generalChecklistPath, cancellationToken).ConfigureAwait(true);
        if (!source.IsSuccess)
        {
            ErrorMessage = Describe(source.Error!);
            return null;
        }

        _source = source.Value;
        var workflow = _context.Store.CurrentWorkflow;
        Tables.Clear();
        foreach (var table in _source.Tables)
        {
            Tables.Add(new ImportTableViewModel(table, Entities, EntityChoices, Strings, workflow?.Fields ?? [], Tables.Count));
        }

        ValueChoices.Clear();
        CrConflicts.Clear();
        CrReferenceChoices.Clear();
        _ownerOptions = [];
        OwnerChoices = [];
        FallbackOwnerIndex = -1;
        return MapStep;
    }

    // ----- Mapping and decisions -----
    private async Task<bool> BuildDraftAsync(CancellationToken cancellationToken)
    {
        if (_source is null || _context.Store.CurrentWorkflow is not { } workflow)
        {
            return false;
        }

        var ledger = await _services.ImportState.LoadLedgerAsync(SourceNamespace.Trim(), cancellationToken).ConfigureAwait(true);
        if (!ledger.IsSuccess)
        {
            ErrorMessage = Describe(ledger.Error!);
            return false;
        }

        var existingCrs = _context.Store.ChangeRequests.Items.ToDictionary(c => c.NormalizedNumber, c => c.Id.Value, StringComparer.Ordinal);
        var time = _services.Time ?? TimeProvider.System;
        var options = new ExternalImportOptions
        {
            SourceNamespace = SourceNamespace.Trim(),
            Mode = ModeIndex == 1 ? ImportMode.Merge : ImportMode.CreateOnly,
            NowUtc = time.GetUtcNow(),
            TimeZone = time.LocalTimeZone,
            Tables = [.. Tables.Select(t => t.ToMapping())],
            ValueChoices = ValueChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (Guid?)c.Values[c.SelectedIndex]),
            CrValueChoices = CrConflicts.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (string?)c.Values[c.SelectedIndex]),
            CrReferenceChoices = CrReferenceChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (string?)c.Values[c.SelectedIndex]),
            FallbackOwnerId = SelectedOwner?.Id,
            FallbackOwnerName = SelectedOwner?.Name,
        };
        var source = _source;
        _draft = await Task.Run(() => ExternalImportPlanner.Build(source, options, workflow, existingCrs, ledger.Value), cancellationToken).ConfigureAwait(true);
        SyncDecisions(_draft, workflow);
        return true;
    }

    private void SyncDecisions(ExternalImportDraft draft, Application.Workflow.WorkflowSnapshot workflow)
    {
        var previousValues = ValueChoices.ToDictionary(c => c.Key, c => c.SelectedIndex);
        ValueChoices.Clear();
        foreach (var value in draft.Values.OrderBy(v => v.Kind).ThenBy(v => v.SourceName, StringComparer.CurrentCultureIgnoreCase))
        {
            var entries = WorkflowLookups.Entries(workflow, value.Kind);
            var labels = new List<string> { Strings.Format("Import_CreateValue", value.SourceName) };
            var ids = new List<object?> { null };
            labels.AddRange(entries.Select(e => Strings.Format("Import_MapToValue", e.Name.Display)));
            ids.AddRange(entries.Select(e => (object?)e.Id));
            var selected = value.ChosenId is { } chosen ? ids.IndexOf(chosen) : 0;
            ValueChoices.Add(new ImportChoiceViewModel(value.Key, Strings.GetString("LookupKind_" + value.Kind), value.SourceName, labels, ids,
                previousValues.GetValueOrDefault(value.Key, Math.Max(0, selected)))
            { AutomationId = "Data_ImportValueCombo_" + ValueChoices.Count.ToString(CultureInfo.InvariantCulture) });
        }

        CrConflicts.Clear();
        foreach (var conflict in draft.CrConflicts)
        {
            CrConflicts.Add(new ImportChoiceViewModel(conflict.Key, conflict.CrNumber, Strings.GetString("Import_CrField_" + conflict.Field),
                [.. conflict.Candidates], [.. conflict.Candidates.Cast<object?>()], conflict.Chosen is { } c ? conflict.Candidates.ToList().IndexOf(c) : -1)
            { AutomationId = "Data_ImportCrConflictCombo_" + CrConflicts.Count.ToString(CultureInfo.InvariantCulture) });
        }

        CrReferenceChoices.Clear();
        foreach (var choice in draft.CrChoices)
        {
            var labels = new List<string> { Strings.GetString("Import_DoNotLink") };
            labels.AddRange(choice.Numbers);
            var values = new List<object?> { null };
            values.AddRange(choice.Numbers);
            var selected = !choice.IsChosen ? -1 : choice.Chosen is { } number ? choice.Numbers.ToList().IndexOf(number) + 1 : 0;
            CrReferenceChoices.Add(new ImportChoiceViewModel(choice.Key, choice.SourceKey, Strings.GetString("Import_CrChoiceHelp"), labels, values, selected)
            { AutomationId = "Data_ImportCrChoiceCombo_" + CrReferenceChoices.Count.ToString(CultureInfo.InvariantCulture) });
        }

        HasNoDecisions = ValueChoices.Count == 0 && CrConflicts.Count == 0 && CrReferenceChoices.Count == 0;
    }

    private (Guid? Id, string? Name)? SelectedOwner =>
        FallbackOwnerIndex >= 0 && FallbackOwnerIndex < _ownerOptions.Count ? _ownerOptions[FallbackOwnerIndex] : null;

    private void SyncOwnerChoices(ExternalImportDraft draft)
    {
        var previous = SelectedOwner;
        var workflow = _context.Store.CurrentWorkflow;
        var existing = (workflow?.People ?? []).Select(p => ((Guid?)p.Id.Value, (string?)null, p.Name.Display));
        var created = draft.Values.Where(v => v is { Kind: LookupKind.Person, ChosenId: null }).Select(v => ((Guid?)null, (string?)v.SourceName, v.SourceName));
        var options = existing.Concat(created).OrderBy(o => o.Item3, StringComparer.CurrentCultureIgnoreCase).ToList();
        _ownerOptions = [.. options.Select(o => (o.Item1, o.Item2))];
        OwnerChoices = [.. options.Select(o => o.Item2 is null ? o.Item3 : Strings.Format("Import_NewOwnerOption", o.Item3))];
        (Guid? Id, string? Name)? proposed = previous ?? (workflow?.Defaults.DefaultCrOwnerId is { } owner ? (owner.Value, null) : null);
        FallbackOwnerIndex = proposed is { } p ? _ownerOptions.IndexOf(p) : -1;
        MissingOwnerText = Strings.Format("Import_MissingOwnerText", draft.MissingOwnerCount);
    }

    // ----- Preview and apply -----
    private async Task<bool> PreviewAsync(TransferDocument document, CancellationToken cancellationToken)
    {
        var mode = ModeIndex == 1 ? ImportMode.Merge : ImportMode.CreateOnly;
        var result = IsExternal && _draft is { } draft
            ? await _services.Import.PreviewAsync(document, mode, cancellationToken, SourceNamespace.Trim(), draft.SourceKeys).ConfigureAwait(true)
            : await _services.Import.PreviewAsync(document, mode, cancellationToken, options: OwnFormatOptions()).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = Describe(result.Error!);
            return false;
        }

        var plan = result.Value;
        SyncRemovals(plan);
        IsConfigurationReplace = plan.IsConfigurationReplace;
        IsReplaceConfirmed = false;
        if (_draft is { Findings.Count: > 0 } withFindings)
        {
            plan = plan with { Findings = [.. withFindings.Findings, .. plan.Findings] };
        }

        _plan = plan;
        _operationId = OperationId.New();
        CountRows = [.. plan.Counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new ImportCountRow(
            Strings.GetString("Transfer_Entity_" + c.Key), Number(c.Value.Create), Number(c.Value.Update), Number(c.Value.Skip), Number(c.Value.Conflict), Number(c.Value.Error)))];
        Findings = [.. plan.Findings.OrderBy(f => f.Severity).Take(MaxFindingRows).Select(f => new ImportFindingRow(
            Strings.GetString("Import_Severity_" + f.Severity),
            f.Severity switch { ImportSeverity.Error => "", ImportSeverity.Conflict => "", ImportSeverity.Warning => "", _ => "" },
            Strings.GetString("Transfer_Entity_" + f.Entity),
            f.RecordKey,
            Strings.Format("Import_Finding_" + f.Code.Replace("-", string.Empty, StringComparison.Ordinal), FindingField(f), f.Detail ?? string.Empty)))];
        var errors = plan.Findings.Count(f => f.Severity == ImportSeverity.Error);
        // ADR 0026: a configuration replace always gets a safety backup first.
        var (readyKey, readyCount) = plan switch
        {
            { IsConfigurationReplace: true, Content: TransferContent.ConfigurationOnly } => ("Import_PreviewReadyConfiguration", plan.TotalWriteCount),
            { IsConfigurationReplace: true } => ("Import_PreviewReadyReplace", plan.RecordWriteCount),
            { IsTargetEmpty: true } => ("Import_PreviewReady", plan.RecordWriteCount),
            _ => ("Import_PreviewReadySafety", plan.RecordWriteCount),
        };
        PreviewSummary = errors > 0
            ? Strings.Format("Import_PreviewBlocked", Number(errors))
            : plan.HasChanges ? Strings.Format(readyKey, Number(readyCount)) : Strings.GetString("Import_PreviewNothing");
        var p = plan.Presence;
        var absent = new (bool Present, string Key)[]
        {
            (p.Tasks, "Tasks"), (p.ChecklistItems, "ChecklistItems"), (p.SubtaskCards, "SubtaskCards"), (p.ChangeRequests, "ChangeRequests"),
            (p.Reviews, "Reviews"), (p.GeneralTodos, "GeneralTodos"), (p.Dates, "Dates"), (p.Notes, "Notes"),
        }.Where(x => !x.Present).Select(x => Strings.GetString("Import_Presence_" + x.Key)).ToList();

        // B-13.4: say what the source does not contain, because it cannot be reconstructed. A configuration-only import
        // imports no records, so there is nothing to say.
        PresenceText = plan.Content == TransferContent.ConfigurationOnly ? string.Empty
            : absent.Count == 0 ? Strings.GetString("Import_PresenceAll") : Strings.Format("Import_PresenceAbsent", string.Join(", ", absent));
        var unmapped = IsExternal ? Tables.Where(t => t.IsImported).SelectMany(t => t.Columns).Where(c => c.IsIgnored).ToList() : [];
        UnmappedText = unmapped.Count == 0 ? string.Empty
            : Strings.Format("Import_UnmappedSummary", Number(unmapped.Count(c => !c.IsDiscarded)), Number(unmapped.Count(c => c.IsDiscarded)));
        RefreshCommands();
        return true;
    }

    private async Task<bool> ApplyAsync(CancellationToken cancellationToken)
    {
        if (_plan is not { CanApply: true } plan)
        {
            return false;
        }

        string? safety = null;
        if (!plan.IsTargetEmpty || plan.IsConfigurationReplace)
        {
            // B-13.9 / T-09: a non-empty target gets an import-safety backup first; no backup, no import. ADR 0026: so
            // does every configuration replace, because the configuration it replaces is data of its own.
            var backup = await _services.Maintenance.CreateBackupAsync("import-safety", cancellationToken).ConfigureAwait(true);
            if (!backup.IsSuccess)
            {
                ErrorMessage = Strings.Format("Import_SafetyBackupFailed", Describe(backup.Error!));
                return false;
            }

            safety = backup.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _services.Import.ApplyAsync(plan, _operationId, cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            // T-05: after an unknown outcome the same operation ID is kept, so a retry cannot apply the import twice.
            if (result.Error is not CommitOutcomeUnknownError)
            {
                _operationId = OperationId.New();
            }

            ErrorMessage = Describe(result.Error!);
            return false;
        }

        var count = Number(result.Value.Value);
        ResultText = plan switch
        {
            { IsConfigurationReplace: true, Content: TransferContent.ConfigurationOnly } => Strings.Format("Import_ResultConfigurationOnly", count, safety ?? string.Empty),
            { IsConfigurationReplace: true } => Strings.Format("Import_ResultConfiguration", Strings.Format("Import_ResultWithSafety", count, safety ?? string.Empty)),
            _ when safety is null => Strings.Format("Import_Result", count),
            _ => Strings.Format("Import_ResultWithSafety", count, safety),
        };

        _context.Announce(ResultText);
        return true;
    }

    [RelayCommand]
    private void Restart()
    {
        _plan = null;
        _draft = null;
        _source = null;
        _document = null;
        ErrorMessage = null;
        Findings = [];
        CountRows = [];
        ClearRemovals();
        IsConfigurationReplace = false;
        IsReplaceConfirmed = false;
        GoTo(SourceStep);
        RefreshCommands();
    }

    // ----- ADR 0026: content and configuration replace -----

    /// <summary>The content choice for a Max Planner file with definitions; a single CSV table keeps its data import.</summary>
    private ImportOptions? OwnFormatOptions() => IsSingleCsv
        ? null
        : new ImportOptions(Content, RemovalChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => c.Values[c.SelectedIndex]));

    private void ClearRemovals()
    {
        RemovalChoices.Clear();
        HasRemovals = false;
    }

    /// <summary>Lists the plan's in-use removals; a choice made earlier for the same value is kept.</summary>
    private void SyncRemovals(ImportPlan plan)
    {
        var previous = RemovalChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => c.Values[c.SelectedIndex]);
        RemovalChoices.Clear();
        foreach (var removal in plan.Removals)
        {
            var kindText = removal.Key.Kind is { } kind
                ? Strings.GetString("LookupKind_" + kind)
                : Strings.Format("Import_RemovalOption", removal.FieldLabel ?? string.Empty);
            var usage = Strings.Format("Import_RemovalUsage", Number(removal.Usage.Tasks), Number(removal.Usage.SubtaskCards), Number(removal.Usage.ChangeRequests), Number(removal.Usage.ReviewItems));
            if (removal.Usage.Defaults != WorkspaceDefaultRoles.None)
            {
                usage = Strings.Format("Import_RemovalUsageDefaults", usage);
            }

            List<string> labels = [];
            List<Guid?> values = [];
            if (removal.CanClear)
            {
                labels.Add(Strings.GetString("Import_RemovalClear"));
                values.Add(null);
            }

            labels.AddRange(removal.Choices.Select(c => c.Name));
            values.AddRange(removal.Choices.Select(c => (Guid?)c.Id));
            var selected = previous.TryGetValue(removal.Key, out var chosen) ? values.IndexOf(chosen)
                : removal.IsResolved ? values.IndexOf(removal.ChosenId) : -1;
            var choice = new ImportRemovalViewModel(removal.Key, kindText, removal.Name, usage, labels, values, selected)
            {
                AutomationId = "Data_ImportRemovalCombo_" + RemovalChoices.Count.ToString(CultureInfo.InvariantCulture),
                AccessibleName = Strings.Format("Import_RemovalComboName", kindText, removal.Name),
            };

            // Next on the Lookups step waits until every removal has a choice.
            choice.PropertyChanged += (_, _) => RefreshCommands();
            RemovalChoices.Add(choice);
        }

        HasRemovals = RemovalChoices.Count > 0;
    }

    // ADR 0024: "people-dropped" names the role with its label ("Designer"), not the transfer member ("DesignerId", or
    // "DesignerIds" from a version 1 file, ADR 0028).
    // A lookup finding names its kind ("Task status"); a field finding names the field or the option's field.
    private string FindingField(ImportFinding finding) => finding switch
    {
        { Code: ImportFindingCodes.PeopleDropped } => Strings.GetString(finding.Field?.StartsWith("Developer", StringComparison.Ordinal) == true ? "Field_Developer" : "Field_Designer"),
        { Entity: ImportEntity.Lookup } when Enum.TryParse<LookupKind>(finding.Field, ignoreCase: false, out var kind) && Enum.IsDefined(kind) => Strings.GetString("LookupKind_" + kind),
        { Entity: ImportEntity.Field, Code: ImportFindingCodes.ConfigurationAdded or ImportFindingCodes.ConfigurationUpdated or ImportFindingCodes.ConfigurationMissing } when finding.Field is null
            => Strings.GetString("Transfer_Entity_field"),
        { Entity: ImportEntity.Field, Code: ImportFindingCodes.ConfigurationRemoved or ImportFindingCodes.RemovalUnresolved or ImportFindingCodes.ConfigurationMissing }
            => Strings.Format("Import_RemovalOption", finding.Field ?? string.Empty),
        _ => finding.Field ?? string.Empty,
    };
}

/// <summary>ADR 0026: one in-use value a configuration replace removes, and the value that replaces it (or Clear).</summary>
/// <param name="SelectedIndex">-1 when nothing is chosen yet (the preview then reports an error).</param>
public sealed partial class ImportRemovalViewModel(
    ConfigurationRemovalKey key,
    string kindText,
    string name,
    string usageText,
    IReadOnlyList<string> options,
    IReadOnlyList<Guid?> values,
    int selectedIndex) : ObservableObject
{
    public ConfigurationRemovalKey Key { get; } = key;

    /// <summary>A positional AutomationId for the replacement ComboBox (design §8.1).</summary>
    public string AutomationId { get; init; } = string.Empty;

    public string AccessibleName { get; init; } = string.Empty;

    /// <summary>"Task status", or "Option of Risk".</summary>
    public string KindText { get; } = kindText;

    public string Name { get; } = name;

    public string UsageText { get; } = usageText;

    public IReadOnlyList<string> Options { get; } = options;

    /// <summary>The replacement of each option; null is Clear.</summary>
    public IReadOnlyList<Guid?> Values { get; } = values;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = selectedIndex;
}

/// <summary>One source table in the Map step: its entity type and the field of each column (B-13.1).</summary>
public sealed partial class ImportTableViewModel : ObservableObject
{
    private readonly IReadOnlyList<ExternalEntity> _entities;
    private readonly ILocalizedStrings _strings;
    private readonly IReadOnlyList<FieldDefinition> _customFields;
    private readonly int _index;

    /// <param name="customFields">The dataset's active custom fields, offered as mapping targets (ADR 0027).</param>
    /// <param name="index">The table's position in the source, which keys its AutomationIds (design §8.1: never display text).</param>
    public ImportTableViewModel(
        ExternalTable table,
        IReadOnlyList<ExternalEntity> entities,
        IReadOnlyList<string> entityChoices,
        ILocalizedStrings strings,
        IReadOnlyList<FieldDefinition> customFields,
        int index = 0)
    {
        Table = table;
        _index = index;
        EntityAutomationId = "Data_ImportEntityCombo_" + index.ToString(CultureInfo.InvariantCulture);
        _entities = entities;
        _strings = strings;
        _customFields = customFields;
        EntityChoices = entityChoices;
        RowCountText = strings.Format("Import_TableRows", table.Rows.Count);
        EntityIndex = Math.Max(0, entities.ToList().IndexOf(table.SuggestedEntity ?? ExternalEntity.Task));
    }

    public ExternalTable Table { get; }

    public string EntityAutomationId { get; }

    public string Name => Table.Name;

    public string RowCountText { get; }

    public IReadOnlyList<string> EntityChoices { get; }

    public ObservableCollection<ImportColumnViewModel> Columns { get; } = [];

    [ObservableProperty]
    public partial int EntityIndex { get; set; } = -1;

    public ExternalTableMapping ToMapping()
    {
        var indexed = Columns.Select((column, index) => (Column: column, Index: index)).ToList();
        return new(Table.Name, Entity, [.. Columns.Select(c => c.Field)])
        {
            CustomFields = indexed.Where(c => c.Column.CustomFieldChoice is not null).ToDictionary(c => c.Index, c => c.Column.CustomFieldChoice!),
            DiscardedColumns = indexed.Where(c => c.Column.IsDiscarded).Select(c => c.Index).ToHashSet(),
        };
    }

    /// <summary>The table is imported (its entity is not Ignore).</summary>
    public bool IsImported => Entity != ExternalEntity.Ignore;

    private ExternalEntity Entity => EntityIndex >= 0 && EntityIndex < _entities.Count ? _entities[EntityIndex] : ExternalEntity.Ignore;

    partial void OnEntityIndexChanged(int value)
    {
        // B-13.2: aliases propose a field for each column; an ambiguous header stays unmapped and is flagged for review.
        // ADR 0027: the active custom fields of the entity follow the built-in fields, then "Create custom field…".
        Columns.Clear();
        var choices = ExternalFieldAliases.FieldsFor(Entity).Select(f => new ImportFieldChoice(_strings.GetString("Import_Field_" + f), f, null)).ToList();
        var target = ExternalFieldAliases.CustomFieldTarget(Entity);
        var fields = _customFields.Where(f => f.Target == target && f.IsActive).OrderBy(f => f.DisplayOrder).ToList();
        choices.AddRange(fields.Select(f => new ImportFieldChoice(_strings.Format("Import_Field_Custom", f.Label.Display), ExternalField.CustomField, f.Id.Value)));
        if (target is not null)
        {
            choices.Add(new ImportFieldChoice(_strings.GetString("Import_Field_CreateCustom"), ExternalField.CustomField, null));
        }

        var proposals = ExternalFieldAliases.Propose(Entity, Table.Columns, Table.Kind);
        var kindChoices = ExternalValues.CreatableKinds.Select(k => _strings.GetString("FieldKind_" + k)).ToList();
        IReadOnlyList<string> unmappedChoices = [_strings.GetString("Import_Unmapped_Keep"), _strings.GetString("Import_Unmapped_Discard")];
        for (var i = 0; i < Table.Columns.Count; i++)
        {
            var values = Table.Rows.Select(r => i < r.Values.Count ? r.Values[i] : null).ToList();
            var sample = values.FirstOrDefault(v => v is { IsEmpty: false });
            var sampleText = sample?.Text ?? (sample?.Items is { } items ? string.Join(", ", items) : string.Empty);
            var header = Table.Columns[i];
            var proposed = proposals[i].Field != ExternalField.Ignore || proposals[i].IsAmbiguous
                ? choices.FindIndex(c => c.Field == proposals[i].Field && c.CustomFieldId is null)
                : choices.FindIndex(c => c.CustomFieldId is { } id && fields.Any(f => f.Id.Value == id && IsSameName(f, header)));
            Columns.Add(new ImportColumnViewModel(header, sampleText.Length > 80 ? sampleText[..80] + "…" : sampleText, choices, Math.Max(0, proposed), proposals[i].IsAmbiguous,
                () => ExternalValues.SuggestKind(values), kindChoices, unmappedChoices, _strings, canKeepHidden: target is not null)
            {
                AutomationId = string.Create(CultureInfo.InvariantCulture, $"Data_ImportFieldCombo_{_index}_{i}"),
                UnmappedAutomationId = string.Create(CultureInfo.InvariantCulture, $"Data_ImportUnmappedCombo_{_index}_{i}"),
                NewFieldLabelAutomationId = string.Create(CultureInfo.InvariantCulture, $"Data_ImportNewFieldLabelBox_{_index}_{i}"),
                NewFieldKindAutomationId = string.Create(CultureInfo.InvariantCulture, $"Data_ImportNewFieldKindCombo_{_index}_{i}"),
            });
        }
    }

    /// <summary>A header that is an existing custom field's label or key proposes that field, so a repeated import reuses it.</summary>
    private static bool IsSameName(FieldDefinition field, string header) =>
        LookupName.Create(header, "Label") is { IsSuccess: true } name
        && (name.Value.Normalized == field.Label.Normalized || string.Equals(header.Trim(), field.Key.Display, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A mapping target in a column's field ComboBox: a built-in field, an existing custom field, or Create.</summary>
/// <param name="CustomFieldId">The existing custom field; null with <see cref="ExternalField.CustomField"/> means Create.</param>
public sealed record ImportFieldChoice(string Text, ExternalField Field, Guid? CustomFieldId);

/// <summary>One source column, the field it maps to, and (ADR 0027) a new field's label and kind or an unmapped column's keep-or-discard choice.</summary>
public sealed partial class ImportColumnViewModel : ObservableObject
{
    private readonly IReadOnlyList<ImportFieldChoice> _choices;

    // Reads every value of the column, so it runs once, when Create custom field… is first chosen.
    private Func<FieldKind>? _suggestKind;

    /// <param name="suggestKind">The kind suggested from the column's values (ADR 0027).</param>
    public ImportColumnViewModel(
        string header,
        string sample,
        IReadOnlyList<ImportFieldChoice> choices,
        int fieldIndex,
        bool isAmbiguous,
        Func<FieldKind> suggestKind,
        IReadOnlyList<string> kindChoices,
        IReadOnlyList<string> unmappedChoices,
        ILocalizedStrings strings,
        bool canKeepHidden = true)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _choices = choices;
        Header = header;
        Sample = sample;
        FieldChoices = [.. choices.Select(c => c.Text)];
        IsAmbiguous = isAmbiguous;
        KindChoices = kindChoices;
        UnmappedChoices = unmappedChoices;
        CanKeepHidden = canKeepHidden;
        NewFieldLabel = header.Trim();
        _suggestKind = suggestKind;
        UnmappedName = strings.Format("Import_UnmappedComboName", header);
        NewFieldLabelName = strings.Format("Import_NewFieldLabelName", header);
        NewFieldKindName = strings.Format("Import_NewFieldKindName", header);
        FieldIndex = fieldIndex;
    }

    public string Header { get; }

    /// <summary><c>Data_ImportFieldCombo_{table}_{column}</c>, positional (design §8.1: never display text).</summary>
    public string AutomationId { get; init; } = string.Empty;

    public string UnmappedAutomationId { get; init; } = string.Empty;

    public string NewFieldLabelAutomationId { get; init; } = string.Empty;

    public string NewFieldKindAutomationId { get; init; } = string.Empty;

    public string UnmappedName { get; }

    public string NewFieldLabelName { get; }

    public string NewFieldKindName { get; }

    public string Sample { get; }

    public IReadOnlyList<string> FieldChoices { get; }

    public IReadOnlyList<string> KindChoices { get; }

    /// <summary>Keep hidden (default) / Discard.</summary>
    public IReadOnlyList<string> UnmappedChoices { get; }

    /// <summary>The header matched more than one field's aliases (B-13.2: ambiguous mappings require review).</summary>
    public bool IsAmbiguous { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIgnored), nameof(IsNewField), nameof(ShowsKeepChoice), nameof(ShowsNotStored))]
    public partial int FieldIndex { get; set; }

    /// <summary>0 Keep hidden (ADR 0027 default), 1 Discard.</summary>
    [ObservableProperty]
    public partial int UnmappedIndex { get; set; }

    /// <summary>The new custom field's label; the column header is proposed.</summary>
    [ObservableProperty]
    public partial string NewFieldLabel { get; set; }

    /// <summary>The new field's kind in <see cref="ExternalValues.CreatableKinds"/>; the kind suggested from the values is proposed.</summary>
    [ObservableProperty]
    public partial int NewFieldKindIndex { get; set; }

    public ExternalField Field => Choice?.Field ?? ExternalField.Ignore;

    public bool IsIgnored => Field == ExternalField.Ignore;

    public bool IsNewField => Choice is { Field: ExternalField.CustomField, CustomFieldId: null };

    /// <summary>The column's values are stored nowhere: the user chose Discard, or its table has no preserved metadata.</summary>
    public bool IsDiscarded => IsIgnored && (!CanKeepHidden || UnmappedIndex == 1);

    /// <summary>
    /// ADR 0027: tasks and CRs keep unmapped values hidden unless discarded; reviews and General to-dos have no preserved
    /// metadata, so their unmapped columns are always discarded.
    /// </summary>
    public bool CanKeepHidden { get; }

    public bool ShowsKeepChoice => IsIgnored && CanKeepHidden;

    public bool ShowsNotStored => IsIgnored && !CanKeepHidden;

    /// <summary>The custom field of a column mapped to one; null otherwise.</summary>
    public ExternalCustomFieldChoice? CustomFieldChoice => Choice switch
    {
        { Field: ExternalField.CustomField, CustomFieldId: { } id } => ExternalCustomFieldChoice.Existing(id),
        { Field: ExternalField.CustomField } => ExternalCustomFieldChoice.New(NewFieldLabel,
            ExternalValues.CreatableKinds[Math.Clamp(NewFieldKindIndex, 0, ExternalValues.CreatableKinds.Count - 1)]),
        _ => null,
    };

    private ImportFieldChoice? Choice => FieldIndex >= 0 && FieldIndex < _choices.Count ? _choices[FieldIndex] : null;

    partial void OnFieldIndexChanged(int value)
    {
        if (IsNewField && _suggestKind is { } suggest)
        {
            _suggestKind = null;
            NewFieldKindIndex = Math.Max(0, ExternalValues.CreatableKinds.ToList().IndexOf(suggest()));
        }
    }
}

/// <summary>A decision in the Lookups and relationships step: a value mapping, a CR-level value, or a task's one CR.</summary>
/// <param name="SelectedIndex">-1 when nothing is chosen yet (the preview then reports an error).</param>
public sealed partial class ImportChoiceViewModel(string key, string label, string detail, IReadOnlyList<string> options, IReadOnlyList<object?> values, int selectedIndex) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>A positional AutomationId for the choice's ComboBox (design §8.1).</summary>
    public string AutomationId { get; init; } = string.Empty;

    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public IReadOnlyList<string> Options { get; } = options;

    public IReadOnlyList<object?> Values { get; } = values;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = selectedIndex;
}

public sealed record ImportCountRow(string Entity, string Create, string Update, string Skip, string Conflict, string Error);

/// <param name="Glyph">Segoe Fluent Icons glyph shown with the severity text (never color alone).</param>
public sealed record ImportFindingRow(string SeverityText, string Glyph, string EntityText, string RecordKey, string Message);
