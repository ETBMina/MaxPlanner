using System.Globalization;

namespace PlatformControls;

/// <summary>Append-only evidence log next to the executable (spike only; the product logs under data/).</summary>
internal static class SpikeLog
{
    private static readonly Lock _gate = new();

    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "spike-diagnostics.log");

    public static void Write(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
        lock (_gate)
        {
            File.AppendAllText(FilePath, line);
        }
    }
}
