using System.Reactive.Linq;
using System.Reactive.Subjects;
using MaxPlanner.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaxPlanner.Presentation.Common.Store;

public enum DayCheckReason
{
    Midnight,
    Activation,
    Resume,

    /// <summary>The system clock or time zone changed; the cached local time zone is refreshed first.</summary>
    ClockOrTimeZoneChange,
}

/// <summary>
/// The <see cref="IDayBoundarySource"/> of T-07 / B-15.9: a timer armed for the next local midnight plus
/// explicit checks that the window raises on activation, resume, and clock or time-zone changes.
/// </summary>
/// <remarks>
/// Uses the same <see cref="TimeProvider"/> as the projection store (ADR 0010), and emits only when the
/// local date actually differs from the last one seen. Every check re-arms the timer, because a clock or
/// time-zone change moves the next midnight and a timer does not advance while the machine sleeps.
/// Checks may come from any thread; emissions are serialized.
/// </remarks>
public sealed partial class LocalDayBoundarySource : IDayBoundarySource, IDisposable
{
    // The timer fires a little after midnight so it never observes the old date and has to re-arm.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Subject<DateOnly> _dates = new();
    private readonly Lock _gate = new();
    private readonly ITimer _timer;
    private DateOnly _current;
    private bool _isDisposed;

    public LocalDayBoundarySource(TimeProvider time, ILogger<LocalDayBoundarySource>? logger = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? NullLogger<LocalDayBoundarySource>.Instance;
        _current = time.LocalToday();
        _timer = time.CreateTimer(_ => Check(DayCheckReason.Midnight), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            Arm();
        }
    }

    public IObservable<DateOnly> LocalDates => _dates.AsObservable();

    /// <summary>The local date most recently observed.</summary>
    public DateOnly CurrentDate
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// The time until the next local midnight in <paramref name="time"/>'s time zone. Where a daylight-saving
    /// change skips midnight, the new day starts at the first valid local time after it.
    /// </summary>
    public static TimeSpan DelayUntilNextLocalMidnight(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var zone = time.LocalTimeZone;
        var nowUtc = time.GetUtcNow();
        var nextMidnight = TimeZoneInfo.ConvertTime(nowUtc, zone).Date.AddDays(1);
        while (zone.IsInvalidTime(nextMidnight))
        {
            nextMidnight = nextMidnight.AddMinutes(15);
        }

        var delay = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextMidnight, zone), TimeSpan.Zero) - nowUtc;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    /// <summary>Re-reads the local date, emits it when it changed, and re-arms the midnight timer.</summary>
    public void Check(DayCheckReason reason)
    {
        if (reason == DayCheckReason.ClockOrTimeZoneChange)
        {
            // TimeZoneInfo.Local is cached for the process lifetime; without this a time-zone change would
            // go unnoticed until restart.
            TimeZoneInfo.ClearCachedData();
        }

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            var today = _time.LocalToday();
            Arm();
            if (today == _current)
            {
                return;
            }

            var previous = _current;
            _current = today;
            Log.DayChanged(_logger, reason, previous, today);

            // Emitted under the gate so concurrent checks (UI thread and timer) never overlap on the subject.
            _dates.OnNext(today);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _timer.Dispose();
            _dates.OnCompleted();
            _dates.Dispose();
        }
    }

    private void Arm() => _timer.Change(DelayUntilNextLocalMidnight(_time) + Slack, Timeout.InfiniteTimeSpan);

    private static partial class Log
    {
        [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Local date changed from {Previous} to {Today} ({Reason}).")]
        public static partial void DayChanged(ILogger logger, DayCheckReason reason, DateOnly previous, DateOnly today);
    }
}
