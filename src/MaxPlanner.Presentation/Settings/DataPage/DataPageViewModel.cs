using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>The Data page workflows (design §4.6, D13), in <c>SelectorBar</c> order.</summary>
public enum DataMode
{
    Import,
    Export,
    Restore,

    /// <summary>Manage backups (proposed fourth item, design §4.6 proposed note).</summary>
    Backups,
}

/// <summary>
/// Settings › Data (B-12, B-13, T-09): Import, Export, Restore, and Manage backups as in-page wizards. Retained with the
/// Settings page for the session; disposing it cancels running work and discards a staged restore.
/// </summary>
public sealed partial class DataPageViewModel : ObservableObject, IDisposable
{
    public DataPageViewModel(WorkspaceContext context, DataServices services)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);
        Import = new ImportWizardViewModel(context, services);
        Export = new ExportWizardViewModel(context.Strings, services);
        Restore = new RestoreWizardViewModel(context.Strings, services);
        Backups = new BackupsViewModel(context, services);
    }

    /// <summary>The breadcrumb's "Settings" crumb was chosen.</summary>
    public event EventHandler? CloseRequested;

    public ImportWizardViewModel Import { get; }

    public ExportWizardViewModel Export { get; }

    public RestoreWizardViewModel Restore { get; }

    public BackupsViewModel Backups { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImport), nameof(IsExport), nameof(IsRestore), nameof(IsBackups))]
    public partial DataMode Mode { get; set; }

    public bool IsImport => Mode == DataMode.Import;

    public bool IsExport => Mode == DataMode.Export;

    public bool IsRestore => Mode == DataMode.Restore;

    public bool IsBackups => Mode == DataMode.Backups;

    /// <summary>Opens a workflow, for example Export preset by the Grid or Backups from Manage backups….</summary>
    public void Show(DataMode mode, ViewExportScope? view = null)
    {
        if (view is not null)
        {
            Export.PresetView(view);
        }

        Mode = mode;
    }

    public void Dispose()
    {
        Import.Dispose();
        Export.Dispose();
        Restore.Dispose();
    }

    partial void OnModeChanged(DataMode value)
    {
        if (value == DataMode.Restore)
        {
            _ = Restore.RefreshAsync();
        }
        else if (value == DataMode.Backups)
        {
            _ = Backups.RefreshAsync();
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
