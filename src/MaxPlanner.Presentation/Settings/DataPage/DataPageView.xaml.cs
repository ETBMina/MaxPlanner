using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>
/// The Data page view. Code-behind handles view mechanics only (AGENTS.md §4): the breadcrumb, the SelectorBar, the
/// backup list selection, and the L/M/S layout (step list at L, step caption at M/S).
/// </summary>
public sealed partial class DataPageView : UserControl
{
    private static readonly Lazy<ResourceStrings> Strings = new(() => new ResourceStrings());
    private readonly ImportWizardView _import;
    private readonly ExportWizardView _export;
    private readonly RestoreWizardView _restore;
    private readonly BackupsView _backups;
    private bool _isSyncingMode;

    public DataPageView(DataPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _import = new ImportWizardView(viewModel.Import);
        _export = new ExportWizardView(viewModel.Export);
        _restore = new RestoreWizardView(viewModel.Restore);
        _backups = new BackupsView(viewModel.Backups);
        ImportHost.Content = _import;
        ExportHost.Content = _export;
        RestoreHost.Content = _restore;
        BackupsHost.Content = _backups;
        Breadcrumb.ItemsSource = new[] { Strings.Value.GetString("Data_BreadcrumbSettings"), Strings.Value.GetString("Data_BreadcrumbData") };
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataPageViewModel.Mode))
            {
                SyncModeBar();
            }
        };
        SyncModeBar();
    }

    public DataPageViewModel ViewModel { get; }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility AtStep(int current, int step) => current == step ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    /// <summary>Current, done, or pending, as distinct glyphs; the "Current step" text says it without the glyph.</summary>
    public static string StepGlyph(bool isCurrent, bool isDone) => isCurrent ? "" : isDone ? "" : "";

    public static string DeleteId(string name) => "Data_BackupDeleteButton_" + name;

    private void ApplyBreakpoint(double width)
    {
        // Design §4.6: the step list sits on the left at L; M/S show "Step 2 of 5 · …" instead.
        var padding = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        var isLarge = width >= 1008;
        HeaderRoot.Padding = new Thickness(padding, isLarge ? 24 : 16, padding, 8);
        _import.SetLayout(isLarge, padding);
        _export.SetLayout(isLarge, padding);
        _restore.SetLayout(isLarge, padding);
        _backups.SetLayout(padding);
    }

    private void SyncModeBar()
    {
        _isSyncingMode = true;
        ModeBar.SelectedItem = ViewModel.Mode switch
        {
            DataMode.Export => ExportItem,
            DataMode.Restore => RestoreItem,
            DataMode.Backups => BackupsItem,
            _ => ImportItem,
        };
        _isSyncingMode = false;
    }

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_isSyncingMode)
        {
            return;
        }

        ViewModel.Mode = sender.SelectedItem == ExportItem ? DataMode.Export
            : sender.SelectedItem == RestoreItem ? DataMode.Restore
            : sender.SelectedItem == BackupsItem ? DataMode.Backups
            : DataMode.Import;
    }

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        // The last crumb ("Data") is the current page and does nothing.
        if (args.Index == 0)
        {
            ViewModel.CloseCommand.Execute(null);
        }
    }
}
