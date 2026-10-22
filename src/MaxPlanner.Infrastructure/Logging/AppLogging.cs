using System.Globalization;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace MaxPlanner.Infrastructure.Logging;

/// <summary>
/// Bounded local log files under <c>data/logs</c> (T-09): at most <see cref="RetainedFileCount"/> files of
/// <see cref="FileSizeLimitBytes"/> each. Nothing is uploaded.
/// </summary>
/// <remarks>
/// Callers log identifiers, versions, timings, and failure categories only, never task titles, notes, or
/// imported content. Disposing the returned factory flushes and closes the files.
/// </remarks>
public static class AppLogging
{
    public const long FileSizeLimitBytes = 5L * 1024 * 1024;

    public const int RetainedFileCount = 5;

    /// <summary>The rolling file name pattern; Serilog inserts the date and, after a size roll, a counter.</summary>
    public const string FileNamePattern = "maxplanner-.log";

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static ILoggerFactory CreateFactory(DataPaths paths) => CreateFactory(paths, FileSizeLimitBytes, LogEventLevel.Information);

    internal static ILoggerFactory CreateFactory(DataPaths paths, long fileSizeLimitBytes, LogEventLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, FileNamePattern),
                outputTemplate: OutputTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCount)
            .CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }
}
