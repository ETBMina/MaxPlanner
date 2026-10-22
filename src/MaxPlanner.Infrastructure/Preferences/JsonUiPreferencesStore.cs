using System.Text.Json;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Infrastructure.Preferences;

/// <summary>
/// <c>data/configuration.json</c> with a last-good copy (T-09). Saves are debounced and written off the
/// caller's thread through a temporary file and <see cref="File.Replace(string, string, string?, bool)"/>.
/// </summary>
public sealed partial class JsonUiPreferencesStore : IUiPreferencesStore, IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    // A real preferences file is a few KiB; anything far larger is not ours and is not parsed.
    private const long MaxFileBytes = 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private UiPreferences _current = UiPreferences.Default;
    private ITimer? _timer;
    private bool _isDirty;

    // False while configuration.json is a file this session could not read. Replacing it then would turn
    // the unreadable file into the last-good copy and lose the copy that is actually good.
    private bool _isMainFileTrusted = true;

    public JsonUiPreferencesStore(DataPaths paths, TimeProvider time, ILogger<JsonUiPreferencesStore>? logger = null, TimeSpan? debounce = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? NullLogger<JsonUiPreferencesStore>.Instance;
        _debounce = debounce ?? DefaultDebounce;
    }

    public UiPreferences Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public PreferencesLoadResult Load()
    {
        var main = TryRead(_paths.ConfigurationFile);
        var result = main.Status switch
        {
            ReadStatus.Ok => new PreferencesLoadResult(main.Preferences!, PreferencesRecovery.None),
            ReadStatus.Missing when !File.Exists(_paths.LastGoodConfigurationFile) => new PreferencesLoadResult(UiPreferences.Default, PreferencesRecovery.None),
            _ => LoadFallback(),
        };

        lock (_gate)
        {
            _isMainFileTrusted = main.Status != ReadStatus.Unreadable;
            _current = result.Preferences;
            _isDirty = false;
        }

        return result;
    }

    public void Update(Func<UiPreferences, UiPreferences> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = change(_current);
            if (Equals(updated, _current))
            {
                return;
            }

            _current = updated;
            _isDirty = true;
            if (_timer is null)
            {
                _timer = _time.CreateTimer(_ => _ = WriteIfDirtyAsync(CancellationToken.None), null, _debounce, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        return WriteIfDirtyAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private PreferencesLoadResult LoadFallback()
    {
        var lastGood = TryRead(_paths.LastGoodConfigurationFile);
        if (lastGood.Status == ReadStatus.Ok)
        {
            Log.RestoredLastGood(_logger);
            return new PreferencesLoadResult(lastGood.Preferences!, PreferencesRecovery.RestoredLastGood);
        }

        Log.ResetToDefaults(_logger);
        return new PreferencesLoadResult(UiPreferences.Default, PreferencesRecovery.ResetToDefaults);
    }

    private ReadOutcome TryRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return new ReadOutcome(ReadStatus.Missing, null);
            }

            if (file.Length > MaxFileBytes)
            {
                Log.Unreadable(_logger, file.Name, "too large", null);
                return new ReadOutcome(ReadStatus.Unreadable, null);
            }

            var document = JsonSerializer.Deserialize(File.ReadAllBytes(path), PreferencesJsonContext.Default.PreferencesDocument);
            if (document?.ConfigurationVersion is not { } version || version < 1 || version > UiPreferences.CurrentVersion)
            {
                Log.Unreadable(_logger, file.Name, "missing or unsupported configurationVersion", null);
                return new ReadOutcome(ReadStatus.Unreadable, null);
            }

            var preferences = PreferencesMapper.ToPreferences(document, out var resets);
            if (resets > 0)
            {
                Log.ValuesReset(_logger, file.Name, resets);
            }

            return new ReadOutcome(ReadStatus.Ok, preferences);
        }
        catch (JsonException ex)
        {
            // The exception text can quote file content; only its position is logged.
            Log.Unreadable(_logger, Path.GetFileName(path), $"invalid JSON at line {ex.LineNumber}", null);
            return new ReadOutcome(ReadStatus.Unreadable, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Unreadable(_logger, Path.GetFileName(path), ex.GetType().Name, ex);
            return new ReadOutcome(ReadStatus.Unreadable, null);
        }
    }

    private async Task WriteIfDirtyAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UiPreferences snapshot;
            bool isMainFileTrusted;
            lock (_gate)
            {
                if (!_isDirty)
                {
                    return;
                }

                snapshot = _current;
                isMainFileTrusted = _isMainFileTrusted;
                _isDirty = false;
            }

            try
            {
                Write(snapshot, isMainFileTrusted);
                lock (_gate)
                {
                    _isMainFileTrusted = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preferences are not business data: a failed save is logged and retried by the next change or flush.
                Log.SaveFailed(_logger, ex);
                lock (_gate)
                {
                    _isDirty = true;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Write(UiPreferences preferences, bool isMainFileTrusted)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var main = _paths.ConfigurationFile;
        var temporary = main + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(PreferencesMapper.ToDocument(preferences), PreferencesJsonContext.Default.PreferencesDocument);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!File.Exists(main))
        {
            File.Move(temporary, main);
        }
        else if (isMainFileTrusted)
        {
            // Atomic swap on the same volume: the previous file becomes the last-good copy.
            File.Replace(temporary, main, _paths.LastGoodConfigurationFile, ignoreMetadataErrors: true);
        }
        else
        {
            // Keep the unreadable file for diagnosis instead of promoting it to last-good.
            File.Move(main, Path.Combine(_paths.DataDirectory, "configuration.unreadable.json"), overwrite: true);
            File.Move(temporary, main);
        }
    }

    private enum ReadStatus
    {
        Ok,
        Missing,
        Unreadable,
    }

    private readonly record struct ReadOutcome(ReadStatus Status, UiPreferences? Preferences);

    private static partial class Log
    {
        [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Preferences file {File} is unreadable ({Reason}).")]
        public static partial void Unreadable(ILogger logger, string file, string reason, Exception? exception);

        [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Preferences were restored from the last good copy.")]
        public static partial void RestoredLastGood(ILogger logger);

        [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Preferences were reset to defaults; no readable copy exists.")]
        public static partial void ResetToDefaults(ILogger logger);

        [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "{Count} invalid value(s) in {File} were reset to defaults.")]
        public static partial void ValuesReset(ILogger logger, string file, int count);

        [LoggerMessage(EventId = 2005, Level = LogLevel.Warning, Message = "Saving preferences failed; the change stays pending.")]
        public static partial void SaveFailed(ILogger logger, Exception exception);
    }
}
