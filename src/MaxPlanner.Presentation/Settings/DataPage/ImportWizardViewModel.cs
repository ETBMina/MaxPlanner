using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Application.Transfer.External;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>
/// Import (B-12, B-13, design §4.6): Source → Map (external) → Lookups and relationships (external) → Preview → Result.
/// The preview writes nothing; Apply takes a safety backup of a non-empty dataset, then commits the reviewed plan in one
/// transaction (T-10). A Max Planner export skips the mapping steps because it carries its own IDs and definitions.
/// </summary>
public sealed partial class ImportWizardViewModel : WizardViewModel
{
    private const int SourceStep = 0;
    private const int MapStep = 1;
    private const int LookupsStep = 2;
    private const int PreviewStep = 3;
    private const int ResultStep = 4;
    private const int MaxFindingRows = 1_000;

    private static readonly IReadOnlyList<ExternalEntity> Entities = [ExternalEntity.Task, ExternalEntity.ChangeRequest, ExternalEntity.Review, ExternalEntity.GeneralTodo, ExternalEntity.Ignore];

    private readonly WorkspaceContext _context;
    private readonly DataServices _services;
    private readonly List<string> _paths = [];
    private string? _generalChecklistPath;
    private ExternalSource? _source;
    private ExternalImportDraft? _draft;
    private ImportPlan? _plan;
    private OperationId _operationId = OperationId.New();

    public ImportWizardViewModel(WorkspaceContext context, DataServices services)
        : base(context.Strings, ["Import_Step_Source", "Import_Step_Map", "Import_Step_Lookups", "Import_Step_Preview", "Import_Step_Result"])
    {
        _context = context;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        EntityChoices = [.. Entities.Select(e => Strings.GetString("Import_Entity_" + e))];
        ModeChoices = [Strings.GetString("Import_Mode_CreateOnly"), Strings.GetString("Import_Mode_Merge")];
    }

    public IReadOnlyList<string> EntityChoices { get; }

    public IReadOnlyList<string> ModeChoices { get; }

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

    [ObservableProperty]
    public partial IReadOnlyList<string> OwnerChoices { get; private set; } = [];

    /// <summary>B-13.7: the owner for CRs without one; the default CR Owner is proposed. 0 = none.</summary>
    [ObservableProperty]
    public partial int FallbackOwnerIndex { get; set; }

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

    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    public override string NextText => Strings.GetString(StepIndex == PreviewStep ? "Import_ApplyButtonText" : "Data_NextButtonText");

    protected override bool CanAdvance => StepIndex switch
    {
        SourceStep => _paths.Count > 0 && (!IsExternal || !string.IsNullOrWhiteSpace(SourceNamespace)),
        PreviewStep => _plan?.CanApply == true && !_context.IsEditingBlocked,
        _ => true,
    };

    private IReadOnlyList<PersonDefinition> People { get; set; } = [];

    partial void OnIsExternalChanged(bool value)
    {
        _paths.Clear();
        _generalChecklistPath = null;
        SourceText = string.Empty;
        GeneralChecklistText = string.Empty;
        RefreshCommands();
    }

    partial void OnSourceNamespaceChanged(string value) => RefreshCommands();

    protected override int PreviousStep(int current) => current == PreviewStep && !IsExternal ? SourceStep : current - 1;

    protected override async Task<int?> AdvanceAsync(CancellationToken cancellationToken)
    {
        switch (StepIndex)
        {
            case SourceStep:
                return IsExternal ? await ReadExternalAsync(cancellationToken).ConfigureAwait(true) : await ReadOwnAsync(cancellationToken).ConfigureAwait(true);
            case MapStep:
                return await BuildDraftAsync(cancellationToken).ConfigureAwait(true) ? LookupsStep : null;
            case LookupsStep:
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
        return await PreviewAsync(document.Value, cancellationToken).ConfigureAwait(true) ? PreviewStep : null;
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
        Tables.Clear();
        foreach (var table in _source.Tables)
        {
            Tables.Add(new ImportTableViewModel(table, Entities, EntityChoices, Strings, Tables.Count));
        }

        ValueChoices.Clear();
        CrConflicts.Clear();
        CrReferenceChoices.Clear();
        var workflow = _context.Store.CurrentWorkflow;
        People = [.. (workflow?.People ?? []).OrderBy(p => p.Name.Display, StringComparer.CurrentCultureIgnoreCase)];
        OwnerChoices = [Strings.GetString("Import_NoFallbackOwner"), .. People.Select(p => p.Name.Display)];
        var defaultOwner = workflow?.Defaults.DefaultCrOwnerId;
        FallbackOwnerIndex = defaultOwner is { } owner ? People.ToList().FindIndex(p => p.Id == owner) + 1 : 0;
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
        var options = new ExternalImportOptions
        {
            SourceNamespace = SourceNamespace.Trim(),
            Mode = ModeIndex == 1 ? ImportMode.Merge : ImportMode.CreateOnly,
            Tables = [.. Tables.Select(t => t.ToMapping())],
            ValueChoices = ValueChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (Guid?)c.Values[c.SelectedIndex]),
            CrValueChoices = CrConflicts.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (string?)c.Values[c.SelectedIndex]),
            CrReferenceChoices = CrReferenceChoices.Where(c => c.SelectedIndex >= 0).ToDictionary(c => c.Key, c => (string?)c.Values[c.SelectedIndex]),
            FallbackOwnerId = FallbackOwnerIndex > 0 && FallbackOwnerIndex <= People.Count ? People[FallbackOwnerIndex - 1].Id.Value : null,
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

    // ----- Preview and apply -----
    private async Task<bool> PreviewAsync(TransferDocument document, CancellationToken cancellationToken)
    {
        var mode = ModeIndex == 1 ? ImportMode.Merge : ImportMode.CreateOnly;
        var result = IsExternal && _draft is { } draft
            ? await _services.Import.PreviewAsync(document, mode, cancellationToken, SourceNamespace.Trim(), draft.SourceKeys).ConfigureAwait(true)
            : await _services.Import.PreviewAsync(document, mode, cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = Describe(result.Error!);
            return false;
        }

        var plan = result.Value;
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
            Strings.Format("Import_Finding_" + f.Code.Replace("-", string.Empty, StringComparison.Ordinal), f.Field ?? string.Empty, f.Detail ?? string.Empty)))];
        var errors = plan.Findings.Count(f => f.Severity == ImportSeverity.Error);
        PreviewSummary = errors > 0
            ? Strings.Format("Import_PreviewBlocked", Number(errors))
            : plan.HasChanges ? Strings.Format(plan.IsTargetEmpty ? "Import_PreviewReady" : "Import_PreviewReadySafety", Number(plan.RecordWriteCount)) : Strings.GetString("Import_PreviewNothing");
        var p = plan.Presence;
        var absent = new (bool Present, string Key)[]
        {
            (p.Tasks, "Tasks"), (p.ChecklistItems, "ChecklistItems"), (p.SubtaskCards, "SubtaskCards"), (p.ChangeRequests, "ChangeRequests"),
            (p.Reviews, "Reviews"), (p.GeneralTodos, "GeneralTodos"), (p.Dates, "Dates"), (p.Notes, "Notes"),
        }.Where(x => !x.Present).Select(x => Strings.GetString("Import_Presence_" + x.Key)).ToList();

        // B-13.4: say what the source does not contain, because it cannot be reconstructed.
        PresenceText = absent.Count == 0 ? Strings.GetString("Import_PresenceAll") : Strings.Format("Import_PresenceAbsent", string.Join(", ", absent));
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
        if (!plan.IsTargetEmpty)
        {
            // B-13.9 / T-09: a non-empty target gets an import-safety backup first; no backup, no import.
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

        ResultText = safety is null
            ? Strings.Format("Import_Result", Number(result.Value.Value))
            : Strings.Format("Import_ResultWithSafety", Number(result.Value.Value), safety);
        _context.Announce(ResultText);
        return true;
    }

    [RelayCommand]
    private void Restart()
    {
        _plan = null;
        _draft = null;
        _source = null;
        ErrorMessage = null;
        Findings = [];
        CountRows = [];
        GoTo(SourceStep);
        RefreshCommands();
    }
}

/// <summary>One source table in the Map step: its entity type and the field of each column (B-13.1).</summary>
public sealed partial class ImportTableViewModel : ObservableObject
{
    private readonly IReadOnlyList<ExternalEntity> _entities;
    private readonly ILocalizedStrings _strings;
    private readonly int _index;

    /// <param name="index">The table's position in the source, which keys its AutomationIds (design §8.1: never display text).</param>
    public ImportTableViewModel(ExternalTable table, IReadOnlyList<ExternalEntity> entities, IReadOnlyList<string> entityChoices, ILocalizedStrings strings, int index = 0)
    {
        Table = table;
        _index = index;
        EntityAutomationId = "Data_ImportEntityCombo_" + index.ToString(CultureInfo.InvariantCulture);
        _entities = entities;
        _strings = strings;
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

    public ExternalTableMapping ToMapping() => new(Table.Name, Entity, [.. Columns.Select(c => c.Field)]);

    private ExternalEntity Entity => EntityIndex >= 0 && EntityIndex < _entities.Count ? _entities[EntityIndex] : ExternalEntity.Ignore;

    partial void OnEntityIndexChanged(int value)
    {
        // B-13.2: aliases propose a field for each column; an ambiguous header stays unmapped and is flagged for review.
        Columns.Clear();
        var fields = ExternalFieldAliases.FieldsFor(Entity);
        var proposals = ExternalFieldAliases.Propose(Entity, Table.Columns);
        for (var i = 0; i < Table.Columns.Count; i++)
        {
            var sample = Table.Rows.Select(r => i < r.Values.Count ? r.Values[i] : null).FirstOrDefault(v => v is { IsEmpty: false });
            var sampleText = sample?.Text ?? (sample?.Items is { } items ? string.Join(", ", items) : string.Empty);
            Columns.Add(new ImportColumnViewModel(Table.Columns[i], sampleText.Length > 80 ? sampleText[..80] + "…" : sampleText, fields,
                [.. fields.Select(f => _strings.GetString("Import_Field_" + f))], Math.Max(0, fields.ToList().IndexOf(proposals[i].Field)), proposals[i].IsAmbiguous)
            {
                AutomationId = string.Create(CultureInfo.InvariantCulture, $"Data_ImportFieldCombo_{_index}_{i}"),
            });
        }
    }
}

/// <summary>One source column and the field it maps to.</summary>
public sealed partial class ImportColumnViewModel(string header, string sample, IReadOnlyList<ExternalField> fields, IReadOnlyList<string> fieldChoices, int fieldIndex, bool isAmbiguous) : ObservableObject
{
    public string Header { get; } = header;

    /// <summary><c>Data_ImportFieldCombo_{table}_{column}</c>, positional (design §8.1: never display text).</summary>
    public string AutomationId { get; init; } = string.Empty;

    public string Sample { get; } = sample;

    public IReadOnlyList<string> FieldChoices { get; } = fieldChoices;

    /// <summary>The header matched more than one field's aliases (B-13.2: ambiguous mappings require review).</summary>
    public bool IsAmbiguous { get; } = isAmbiguous;

    [ObservableProperty]
    public partial int FieldIndex { get; set; } = fieldIndex;

    public ExternalField Field => FieldIndex >= 0 && FieldIndex < fields.Count ? fields[FieldIndex] : ExternalField.Ignore;
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
