using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MaxPlanner.Presentation.Common.Services;

public enum SaveState
{
    Idle,
    Saving,
    Saved,
    Failed,
    Resyncing,
}

/// <summary>
/// The status-bar save indicator (design §2.8). It reports actual persistence results only: Saving while a
/// command is in flight, Saved after a committed result, Failed after a failed one. Use it on the UI thread.
/// </summary>
/// <remarks>
/// Several commands may be in flight (for example a checklist toggle while a Grid cell commits); the
/// indicator shows Saving until all of them have finished. A failure stays visible until the next success.
/// Resyncing (T-07) takes precedence over every other state while the store reloads.
/// </remarks>
public sealed partial class SaveStatusViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SavedDisplayTime = TimeSpan.FromSeconds(3);

    // A result with a follow-up action ("— Open card") stays long enough to be reached by keyboard.
    private static readonly TimeSpan ResultDisplayTime = TimeSpan.FromSeconds(15);

    private readonly ILocalizedStrings _strings;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _savedTimer;
    private int _inFlight;
    private SaveState _lastResult = SaveState.Idle;
    private bool _isResyncing;
    private string? _resultMessage;
    private Func<Task>? _resultAction;

    public SaveStatusViewModel(ILocalizedStrings strings, IUiDispatcher dispatcher, TimeProvider time)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        Text = _strings.GetString("SaveStatus_Idle");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaving), nameof(IsFailed), nameof(IsSettled), nameof(IsResyncing))]
    public partial SaveState State { get; private set; } = SaveState.Idle;

    [ObservableProperty]
    public partial string Text { get; private set; }

    /// <summary>The last failure's message, kept for the App InfoBar opened by Details.</summary>
    [ObservableProperty]
    public partial string? FailureMessage { get; private set; }

    /// <summary>"Operation 0192…": matches what the user saw to a log entry (design §2.5 Details).</summary>
    [ObservableProperty]
    public partial string? FailureDetails { get; private set; }

    /// <summary>
    /// The link text of the last result's follow-up, for example "Open card" after a pop out (design §4.5);
    /// null when there is none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultAction))]
    public partial string? ResultActionText { get; private set; }

    public bool HasResultAction => ResultActionText is not null;

    /// <summary>Bound two-way to the App InfoBar so the user can close it.</summary>
    [ObservableProperty]
    public partial bool IsFailureNoticeOpen { get; set; }

    public bool IsSaving => State == SaveState.Saving;

    public bool IsFailed => State == SaveState.Failed;

    public bool IsResyncing => State == SaveState.Resyncing;

    /// <summary>Idle or Saved: the CheckMark glyph is shown.</summary>
    public bool IsSettled => State is SaveState.Idle or SaveState.Saved;

    /// <summary>Marks a command as started. Pair every call with <see cref="Complete"/> or <see cref="Fail"/>.</summary>
    public void Begin()
    {
        ClearResult();
        _inFlight++;
        CancelSavedTimer();
        Refresh();
    }

    public void Complete() => CompleteCore(SavedDisplayTime);

    /// <summary>
    /// Completes a command whose result names what happened, optionally with a follow-up link: "Moved to a
    /// Subtask card in Backlog — Open card" (design §4.5). The next command replaces it.
    /// </summary>
    public void Complete(string message, string? actionText, Func<Task>? action)
    {
        ArgumentNullException.ThrowIfNull(message);
        CompleteCore(ResultDisplayTime);
        _resultMessage = message;
        _resultAction = action;
        ResultActionText = action is null ? null : actionText;
        Refresh();
    }

    private void CompleteCore(TimeSpan displayTime)
    {
        _inFlight = Math.Max(0, _inFlight - 1);
        _lastResult = SaveState.Saved;
        FailureMessage = null;
        FailureDetails = null;
        IsFailureNoticeOpen = false;
        Refresh();
        if (State == SaveState.Saved)
        {
            // "Saved" is a transient confirmation; the settled state reads "All changes saved" (design §2.8).
            _savedTimer = _time.CreateTimer(_ => _dispatcher.Post(OnSavedTimerElapsed), null, displayTime, Timeout.InfiniteTimeSpan);
        }
    }

    public void Fail(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        _inFlight = Math.Max(0, _inFlight - 1);
        ClearResult();
        _lastResult = SaveState.Failed;
        CancelSavedTimer();
        FailureMessage = failure.Message;
        FailureDetails = _strings.Format("SaveStatus_OperationDetails", failure.OperationId);

        // Storage problems affect every save, so they also open the App InfoBar (design §2.7); field and
        // conflict errors are shown by the editor that owns the draft.
        IsFailureNoticeOpen = failure.IsAppScope;
        Refresh();
    }

    /// <summary>
    /// The command was refused by validation, so nothing was attempted against the data. The field or
    /// editor shows the error (design §5.0); the indicator keeps the last actual persistence result.
    /// </summary>
    public void Reject()
    {
        _inFlight = Math.Max(0, _inFlight - 1);
        Refresh();
    }

    /// <summary>Ends a command with its typed outcome: validation refusals are <see cref="Reject"/>ed, others <see cref="Fail"/>.</summary>
    public void Report(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Error is MaxPlanner.Application.Common.ValidationFailedError)
        {
            Reject();
        }
        else
        {
            Fail(failure);
        }
    }

    /// <summary>T-07: while the store reloads after a publication fault, the indicator says so.</summary>
    public void SetResyncing(bool isResyncing)
    {
        if (_isResyncing == isResyncing)
        {
            return;
        }

        _isResyncing = isResyncing;
        if (!isResyncing && _lastResult == SaveState.Saved)
        {
            _lastResult = SaveState.Idle;
        }

        Refresh();
    }

    public void Dispose() => CancelSavedTimer();

    [RelayCommand]
    private void ShowDetails() => IsFailureNoticeOpen = FailureMessage is not null;

    [RelayCommand]
    private async Task RunResultActionAsync()
    {
        var action = _resultAction;
        ClearResult();
        Refresh();
        if (action is not null)
        {
            await action().ConfigureAwait(true);
        }
    }

    private void ClearResult()
    {
        _resultMessage = null;
        _resultAction = null;
        ResultActionText = null;
    }

    private void OnSavedTimerElapsed()
    {
        CancelSavedTimer();
        ClearResult();
        if (_lastResult == SaveState.Saved)
        {
            _lastResult = SaveState.Idle;
            Refresh();
        }
    }

    private void CancelSavedTimer()
    {
        _savedTimer?.Dispose();
        _savedTimer = null;
    }

    private void Refresh()
    {
        State = _isResyncing ? SaveState.Resyncing : _inFlight > 0 ? SaveState.Saving : _lastResult;
        if (_resultMessage is not null && State == SaveState.Saved)
        {
            Text = _resultMessage;
            return;
        }

        Text = _strings.GetString(State switch
        {
            SaveState.Saving => "SaveStatus_Saving",
            SaveState.Saved => "SaveStatus_Saved",
            SaveState.Failed => "SaveStatus_Failed",
            SaveState.Resyncing => "SaveStatus_Resyncing",
            _ => "SaveStatus_Idle",
        });
    }
}
