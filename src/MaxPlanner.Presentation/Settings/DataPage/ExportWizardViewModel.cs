using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>
/// Export (B-12, design §4.6): Scope (entire dataset or the current view) → Format → Export (native save picker, then a
/// temporary file replaced on success) → Result with counts, path, Open folder, and the format's limitations.
/// </summary>
public sealed partial class ExportWizardViewModel : WizardViewModel
{
    private const int ScopeStep = 0;
    private const int FormatStep = 1;
    private const int ExportStep = 2;
    private const int ResultStep = 3;

    private readonly DataServices _services;
    private ViewExportScope? _view;

    public ExportWizardViewModel(ILocalizedStrings strings, DataServices services)
        : base(strings, ["Export_Step_Scope", "Export_Step_Format", "Export_Step_Export", "Export_Step_Result"])
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        FormatChoices = [strings.GetString("Export_Format_Json"), strings.GetString("Export_Format_Xlsx"), strings.GetString("Export_Format_CsvTable"), strings.GetString("Export_Format_CsvBundle")];
        TableChoices = [.. Tables.Select(t => strings.GetString("Transfer_Table_" + t))];
    }

    private static IReadOnlyList<TransferTable> Tables { get; } =
        [TransferTable.Tasks, TransferTable.ChangeRequests, TransferTable.ChecklistItems, TransferTable.SubtaskCards, TransferTable.Reviews, TransferTable.GeneralTodos];

    public IReadOnlyList<string> FormatChoices { get; }

    public IReadOnlyList<string> TableChoices { get; }

    /// <summary>"Current view results" is available only when a page's Export… preset a view (B-12).</summary>
    [ObservableProperty]
    public partial bool IsViewAvailable { get; private set; }

    [ObservableProperty]
    public partial string ViewDescription { get; private set; } = string.Empty;

    /// <summary>True: entire dataset (archived tasks and every dependency included); false: the current view.</summary>
    [ObservableProperty]
    public partial bool IsEntireDataset { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsvTable), nameof(FormatHelp))]
    public partial int FormatIndex { get; set; }

    [ObservableProperty]
    public partial int TableIndex { get; set; }

    public bool IsCsvTable => FormatIndex == 2;

    /// <summary>The selected format's limitations, explained before export (design §4.6).</summary>
    public string FormatHelp => Strings.GetString("Export_FormatHelp_" + Format);

    [ObservableProperty]
    public partial string ResultSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> ResultLines { get; private set; } = [];

    public override string NextText => Strings.GetString(StepIndex == ExportStep ? "Export_ExportButtonText" : "Data_NextButtonText");

    protected override bool CanAdvance => StepIndex != ScopeStep || IsEntireDataset || IsViewAvailable;

    private TransferFileFormat Format => FormatIndex switch
    {
        1 => TransferFileFormat.Xlsx,
        2 => TransferFileFormat.CsvTable,
        3 => TransferFileFormat.CsvBundle,
        _ => TransferFileFormat.Json,
    };

    /// <summary>Grid Export… (B-12): presets "Current view results" with the view's entity type and filters.</summary>
    public void PresetView(ViewExportScope view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        IsViewAvailable = true;
        IsEntireDataset = false;
        ViewDescription = Strings.Format("Export_ViewDescription", Number(view.TaskIds.Count), Number(view.SubtaskCardIds.Count), view.Description);
        Restart();
    }

    partial void OnIsEntireDatasetChanged(bool value) => RefreshCommands();

    protected override async Task<int?> AdvanceAsync(CancellationToken cancellationToken)
    {
        if (StepIndex != ExportStep)
        {
            return StepIndex + 1;
        }

        var (extension, label) = Format switch
        {
            TransferFileFormat.Xlsx => (".xlsx", "Export_FileType_Xlsx"),
            TransferFileFormat.CsvTable => (".csv", "Export_FileType_Csv"),
            TransferFileFormat.CsvBundle => (".zip", "Export_FileType_Zip"),
            _ => (".json", "Export_FileType_Json"),
        };
        var suggested = Strings.Format("Export_SuggestedName", DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var path = await _services.Pickers.PickSaveFileAsync(suggested, Strings.GetString(label), extension).ConfigureAwait(true);
        if (path is null)
        {
            return null;
        }

        TransferExportScope scope = IsEntireDataset || _view is null ? EntireDatasetScope.Instance : _view;
        var table = Format == TransferFileFormat.CsvTable ? Tables[Math.Clamp(TableIndex, 0, Tables.Count - 1)] : (TransferTable?)null;
        var result = await _services.Export.ExportAsync(scope, Format, table, path, cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorMessage = Describe(result.Error!);
            return null;
        }

        var report = result.Value;
        ResultPath = report.Path;
        ResultSummary = Strings.Format(report.IsViewExport ? "Export_ResultView" : "Export_ResultEntire", Strings.GetString("Export_Format_" + report.Format));
        ResultLines =
        [
            .. report.Counts.Select(c => Strings.Format("Export_ResultCount", Strings.GetString("Transfer_Entity_" + c.Key), Number(c.Value))),
            .. report.Limitations.Select(l => Strings.GetString("Export_Limitation_" + l.Replace("-", string.Empty, StringComparison.Ordinal))),
        ];
        return ResultStep;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ResultPath.Length > 0)
        {
            _services.Maintenance.ShowInFolder(ResultPath);
        }
    }

    [RelayCommand]
    private void Restart()
    {
        ErrorMessage = null;
        ResultLines = [];
        GoTo(ScopeStep);
    }
}
