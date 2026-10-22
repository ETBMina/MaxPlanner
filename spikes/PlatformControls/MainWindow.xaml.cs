using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PlatformControls.Projection;
using Windows.Graphics;

namespace PlatformControls;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var (widthDip, heightDip) = ParseSizeArgument() ?? (1200, 860);
        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));

        _statusTimer.Tick += (_, _) => StoreStatusText.Text = SpikeHost.DescribeStoreState();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();

        var args = Environment.GetCommandLineArgs();
        var startPage = args.Contains("--projection", StringComparer.OrdinalIgnoreCase)
            ? ProjectionItem
            : args.Contains("--blank", StringComparer.OrdinalIgnoreCase) ? BlankItem : SettingsItem;
#if PAYLOAD_BASELINE
        startPage = ProjectionItem;
#endif
        Nav.SelectedItem = startPage;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var pageType = tag switch
        {
#if !PAYLOAD_BASELINE
            "settings" => typeof(Settings.SettingsSpikePage),
#endif
            "projection" => typeof(ProjectionPage),
            _ => typeof(Page),
        };
        SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"Navigate -> {tag}"));
        ContentFrame.Navigate(pageType);
    }

    /// <summary>"--size 1000x800" (DIPs) lets UI runs reproduce exact window widths without pointer resizing.</summary>
    private static (int Width, int Height)? ParseSizeArgument()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, "--size", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var parts = args[index + 1].Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? (width, height)
            : null;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hWnd);
}
