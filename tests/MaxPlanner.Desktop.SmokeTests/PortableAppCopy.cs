using System.Diagnostics;
using System.Reflection;

namespace MaxPlanner.Desktop.SmokeTests;

/// <summary>
/// One copy of the built app in a temp folder whose path has a space and non-ASCII text, shared by the
/// tests of a class. Each test starts from an empty or arranged <c>data/</c> folder of its own making.
/// </summary>
public sealed class PortableAppCopy : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    public PortableAppCopy()
    {
        var source = typeof(PortableAppCopy).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DesktopOutputDirectory").Value!;
        source = Path.GetFullPath(source);
        if (!File.Exists(Path.Combine(source, "MaxPlanner.exe")))
        {
            throw new InvalidOperationException($"Build the solution first; MaxPlanner.exe was not found in {source}.");
        }

        Root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Smoke", Guid.NewGuid().ToString("N"));
        Copy(source, Root);
    }

    public string Root { get; }

    public string Executable => Path.Combine(Root, "MaxPlanner.exe");

    public string DataDirectory => Path.Combine(Root, "data");

    public string DatabaseFile => Path.Combine(DataDirectory, "data.db");

    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    public void ResetData()
    {
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Launches the app, waits until startup reaches the ready view or the recovery page (read from its own
    /// log), then closes it the way the title-bar Close button does (WM_CLOSE to its own main window).
    /// </summary>
    public LaunchResult LaunchAndClose()
    {
        var logsBefore = ReadLogs();
        using var process = Process.Start(new ProcessStartInfo(Executable) { WorkingDirectory = Path.GetTempPath(), UseShellExecute = false })!;
        try
        {
            var deadline = DateTime.UtcNow + StartupTimeout;
            string outcome;
            while (true)
            {
                var log = ReadLogs()[logsBefore.Length..];
                var line = log.Split('\n').FirstOrDefault(l => l.Contains("Startup ready:", StringComparison.Ordinal) || l.Contains("Startup recovery:", StringComparison.Ordinal));
                process.Refresh();
                if (line is not null && process.MainWindowHandle != 0)
                {
                    outcome = line.Trim();
                    break;
                }

                if (process.HasExited)
                {
                    throw new InvalidOperationException($"MaxPlanner exited early with code {process.ExitCode}. Log:\n{log}");
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Startup did not finish within {StartupTimeout}. Log:\n{log}");
                }

                Thread.Sleep(200);
            }

            // Let the daily backup (started after Ready) finish so the run is deterministic.
            Thread.Sleep(1000);
            process.CloseMainWindow();
            if (!process.WaitForExit(ExitTimeout))
            {
                throw new TimeoutException("MaxPlanner did not exit after WM_CLOSE.");
            }

            return new LaunchResult(process.ExitCode, outcome, ReadLogs()[logsBefore.Length..]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: temp folders are disposable.
        }
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            // A developer's own run of the build output must never travel into the test copy.
            if (!string.Equals(Path.GetFileName(directory), "data", StringComparison.OrdinalIgnoreCase))
            {
                Copy(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }

    private string ReadLogs()
    {
        var logs = Path.Combine(DataDirectory, "logs");
        if (!Directory.Exists(logs))
        {
            return string.Empty;
        }

        // The app keeps the current file open for writing; read it with full sharing.
        return string.Concat(Directory.GetFiles(logs, "*.log").Order(StringComparer.Ordinal).Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }
}

/// <param name="Outcome">The log line that ended startup ("Startup ready: …" or "Startup recovery: …").</param>
/// <param name="Log">Everything this launch logged.</param>
public sealed record LaunchResult(int ExitCode, string Outcome, string Log);
