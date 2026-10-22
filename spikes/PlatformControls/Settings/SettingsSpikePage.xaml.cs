using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PlatformControls.Settings;

public sealed partial class SettingsSpikePage : Page
{
    private int _clickCount;
    private FrameworkElement? _root;

    public SettingsSpikePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public IReadOnlyList<StatusRow> Statuses { get; } = StatusRow.CreateSample();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _root = XamlRoot.Content as FrameworkElement;
        if (_root is null)
        {
            return;
        }

        ThemeCombo.SelectedIndex = _root.RequestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        _root.ActualThemeChanged += OnRootActualThemeChanged;
        UpdateThemeText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_root is not null)
        {
            _root.ActualThemeChanged -= OnRootActualThemeChanged;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Spike 4.2 / AC-23: the theme applies at runtime through the root element's RequestedTheme.
        if (_root is null)
        {
            return;
        }

        _root.RequestedTheme = ThemeCombo.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateThemeText();
        SpikeLog.Write(ActualThemeText.Text);
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args) => UpdateThemeText();

    private void UpdateThemeText()
    {
        if (_root is not null)
        {
            ActualThemeText.Text = $"Requested theme: {_root.RequestedTheme}; actual theme: {_root.ActualTheme}";
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => ReportClick("Open folder button");

    private void OnDataPageCardClick(object sender, RoutedEventArgs e) => ReportClick("Import, export and restore card");

    private void ReportClick(string source)
    {
        _clickCount++;
        ClickStatusText.Text = string.Create(CultureInfo.InvariantCulture, $"{source} clicked ({_clickCount}).");
        SpikeLog.Write(ClickStatusText.Text);
    }
}
