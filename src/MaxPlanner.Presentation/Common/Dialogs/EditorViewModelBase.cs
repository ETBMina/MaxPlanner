using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>
/// The draft-editor state machine shared by the task, CR, Subtask card, and review editors (B-02.8–9, T-05,
/// design §5.0, §5.8, §5.10): save with an idempotent retry, the dirty-close prompt and the window-close guard,
/// close for a follow-up, failure display, conflict state, and the subscription to committed changes.
/// Use on the UI thread.
/// </summary>
/// <remarks>
/// <para>Each editor keeps what is its own: its fields and how they load from a record, how it tells it is dirty,
/// its field errors, the command a save sends, its announcements, and its inline steps.</para>
/// <para>Save runs in this order: guard (<see cref="CanSave"/>), <see cref="CommitPendingInput"/>, a clean existing
/// record just closes, editing blocked, clear errors, <see cref="TryPrepareSave"/>, then one command under an
/// operation ID that is reused only after an unknown outcome (T-05).</para>
/// </remarks>
/// <typeparam name="TFollowUp">What the editor asks for after it closes; its default value is "none".</typeparam>
public abstract partial class EditorViewModelBase<TFollowUp> : ObservableObject, IDialogViewModel, IDirtyCloseEditor, IDisposable
    where TFollowUp : struct, Enum
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly string _recordKey;
    private IDisposable? _commits;
    private bool _closeApproved;
    private OperationId? _retryOperation;

    /// <param name="recordKey">Resource key of the record noun in failure texts, for example <c>Record_Task</c>.</param>
    protected EditorViewModelBase(WorkspaceContext context, bool isNew, string recordKey)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        IsNew = isNew;
        _recordKey = recordKey;
    }

    public event EventHandler? CloseRequested;

    /// <summary>Asks the view to focus a field key such as <c>Title</c> (design §5.0).</summary>
    public event EventHandler<string>? FocusRequested;

    public bool IsNew { get; }

    /// <summary>What the editor asked for when it closed (design §5.0: dialogs never stack).</summary>
    public TFollowUp FollowUp { get; protected set; }

    public string CloseButtonText => Context.Strings.GetString("Common_Cancel");

    /// <summary>"Fix 2 fields to save" (design §5.0); shown in the Error InfoBar at the top.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationSummary))]
    public partial string? ValidationSummary { get; protected set; }

    /// <summary>A non-field failure (storage, conflict text, deleted elsewhere); the draft is kept (AC-07).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; protected set; }

    [ObservableProperty]
    public partial bool IsSaving { get; protected set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsDirtyClosePromptOpen { get; private set; }

    /// <summary>T-05: the record changed since this editor loaded it; the draft is kept for review or reload.</summary>
    [ObservableProperty]
    public partial bool IsConflict { get; protected set; }

    [ObservableProperty]
    public partial bool IsDeletedElsewhere { get; protected set; }

    public bool HasValidationSummary => ValidationSummary is not null;

    public bool HasSaveError => SaveError is not null;

    /// <inheritdoc/>
    public bool IsWindowClosePending { get; private set; }

    public abstract bool CanSave { get; }

    protected WorkspaceContext Context { get; }

    /// <summary>True while the editor loads a record into its fields; dirty tracking waits until it ends.</summary>
    protected bool IsLoading { get; set; }

    /// <summary>Whether closing now would lose something and must ask first (design §5.8).</summary>
    protected abstract bool NeedsClosePrompt { get; }

    /// <summary>Whether Save of an existing record has anything to send; a clean draft just closes.</summary>
    protected virtual bool HasChangesToSave => IsDirty;

    /// <summary>The failure text for a revision conflict on this editor's record.</summary>
    protected virtual string ConflictMessage => Context.Strings.Format("Editor_ConflictMessage", Context.Strings.GetString(_recordKey));

    /// <summary>
    /// Saves the whole draft as one command (B-02.8). Repeated calls while saving are ignored. On success the editor
    /// closes once the result is displayed; on failure the draft, its errors, and the dialog stay.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanSave)
        {
            return false;
        }

        CommitPendingInput();
        if (!IsNew && !HasChangesToSave)
        {
            Approve();
            return true;
        }

        var saved = await SaveDraftAsync().ConfigureAwait(true);
        if (saved)
        {
            Approve();
        }

        return saved;
    }

    /// <summary>
    /// Esc, Cancel, an outside click or a follow-up. A draft with changes cancels the close and asks for the Save /
    /// Discard / Continue editing confirmation (design §5.8).
    /// </summary>
    /// <returns>True when the dialog may close.</returns>
    public bool TryClose()
    {
        if (_closeApproved)
        {
            return true;
        }

        if (IsSaving)
        {
            return false;
        }

        if (NeedsClosePrompt)
        {
            IsDirtyClosePromptOpen = true;
            return false;
        }

        _closeApproved = true;
        return true;
    }

    public async Task<bool> SaveFromPromptAsync()
    {
        IsDirtyClosePromptOpen = false;
        var saved = await SaveAsync().ConfigureAwait(true);
        if (!saved)
        {
            // A failed save keeps the editor open; the window close the prompt answered is abandoned.
            IsWindowClosePending = false;
        }

        return saved;
    }

    /// <inheritdoc/>
    public bool TryCloseForWindow()
    {
        if (TryClose())
        {
            return true;
        }

        IsWindowClosePending = IsDirtyClosePromptOpen;
        return false;
    }

    /// <summary>Discard: closes without saving; nothing is created, including new people and tags (B-02.6).</summary>
    public void DiscardChanges()
    {
        IsDirtyClosePromptOpen = false;
        Approve();
    }

    /// <summary>Continue editing (also Esc on the prompt): the draft stays and any pending follow-up is dropped.</summary>
    public void ContinueEditing()
    {
        IsDirtyClosePromptOpen = false;
        IsWindowClosePending = false;
        FollowUp = default;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The save pipeline without the close: editing blocked, clear errors, <see cref="TryPrepareSave"/>, then the command.
    /// The operation ID of an unknown outcome is reused by the next attempt (T-05).
    /// </summary>
    /// <returns>True when the command committed and its result is displayed.</returns>
    protected async Task<bool> SaveDraftAsync()
    {
        if (Context.IsEditingBlocked)
        {
            SaveError = Context.Strings.GetString("Error_EditingBlocked");
            RaiseState();
            return false;
        }

        ClearErrors();
        if (!TryPrepareSave())
        {
            RaiseState();
            return false;
        }

        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        IsDirtyClosePromptOpen = false;
        RaiseState();
        Context.SaveStatus.Begin();
        var (error, sequence) = await ExecuteSaveAsync(operation).ConfigureAwait(true);
        if (error is not null)
        {
            var failure = CommandFailure.From(error, operation, _recordKey, Context.Strings);
            Context.SaveStatus.Report(failure);

            // T-05: only an unknown outcome is retried under the same operation; anything else is a new intent.
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            IsSaving = false;
            ShowFailure(failure);
            RaiseState();
            return false;
        }

        Context.SaveStatus.Complete();
        _retryOperation = null;
        await WhenDisplayedAsync(sequence).ConfigureAwait(true);
        IsSaving = false;
        OnSaved();
        return true;
    }

    /// <summary>Sends the draft as one command under <paramref name="operation"/>.</summary>
    /// <returns>The failure, or null and the commit sequence to wait for.</returns>
    protected abstract Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation);

    /// <summary>Text still in an input box counts as typed, as when Save comes from Ctrl+Enter before the box loses focus.</summary>
    protected virtual void CommitPendingInput()
    {
    }

    /// <summary>Checks what must be valid before the command is sent; on false the editor shows why.</summary>
    protected virtual bool TryPrepareSave() => true;

    /// <summary>Called after a successful save, before the editor closes; announces the result.</summary>
    protected abstract void OnSaved();

    /// <summary>Clears the editor's own field errors; the summary and the save error are cleared with them.</summary>
    protected abstract void ClearFieldErrors();

    /// <summary>Shows validation errors under their fields, sets the summary, and asks for focus on the first.</summary>
    protected abstract void ShowFieldErrors(IReadOnlyList<DomainError> errors);

    /// <summary>
    /// Shows a failed save (design §5.0): field errors, a revision conflict (kept for review), or the failure text, then
    /// announces it. <see cref="ShowEditorFailure"/> handles the editor's own failures first.
    /// </summary>
    protected virtual void ShowFailure(CommandFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!ShowEditorFailure(failure))
        {
            switch (failure.Error)
            {
                case ValidationFailedError validation:
                    ShowFieldErrors(validation.Errors);
                    break;
                case ConcurrencyConflictError:
                    IsConflict = true;
                    SaveError = ConflictMessage;
                    break;
                default:
                    SaveError = failure.Message;
                    break;
            }
        }

        Context.Announce(failure.Message, isFailure: true);
    }

    /// <summary>Shows a failure only this editor knows (for example a duplicate CR number).</summary>
    /// <returns>True when it was shown; the announcement follows either way.</returns>
    protected virtual bool ShowEditorFailure(CommandFailure failure) => false;

    /// <summary>"Fix 1 field to save" or "Fix 2 fields to save", and focus on the first field in error (design §5.0).</summary>
    protected void ShowValidationSummary(int fieldCount, string? focusKey)
    {
        ValidationSummary = Context.Strings.Format(fieldCount == 1 ? "TaskEditor_FixField" : "TaskEditor_FixFields", fieldCount);
        if (focusKey is not null)
        {
            RequestFocus(focusKey);
        }
    }

    protected void RequestFocus(string key) => FocusRequested?.Invoke(this, key);

    /// <summary>The conflict is resolved (reloaded or applied): the conflict flag and its message are cleared.</summary>
    protected void EndConflict()
    {
        IsConflict = false;
        SaveError = null;
    }

    /// <summary>Whether the draft differs from the record it was loaded from.</summary>
    protected abstract bool ComputeIsDirty();

    protected virtual void RecomputeDirty()
    {
        if (!IsLoading)
        {
            IsDirty = ComputeIsDirty();
        }
    }

    /// <summary>Closes this dialog first (after the dirty prompt), then the caller opens what <paramref name="followUp"/> names.</summary>
    protected void CloseForFollowUp(TFollowUp followUp)
    {
        if (IsSaving)
        {
            return;
        }

        FollowUp = followUp;
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The editor may close now without asking (after a save, a discard, or a step that leaves the editor).</summary>
    protected void Approve()
    {
        _closeApproved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    protected Task WhenDisplayedAsync(long sequence) => Context.WhenDisplayedAsync(sequence, DisplayTimeout);

    /// <summary>Starts following committed changes; call once, at the end of the editor's constructor.</summary>
    /// <exception cref="InvalidOperationException">The editor already follows committed changes.</exception>
    protected void ObserveCommits()
    {
        if (_commits is not null)
        {
            throw new InvalidOperationException("The editor already follows committed changes.");
        }

        _commits = Context.Store.Committed.ObserveOn(Context.UiScheduler).Subscribe(_ => OnCommitted());
    }

    /// <summary>A commit reached the store: follow it while the draft is clean, or notice a deletion.</summary>
    protected abstract void OnCommitted();

    /// <summary>Raises the editor's derived state properties after a state change.</summary>
    protected virtual void RaiseState()
    {
    }

    /// <summary>
    /// Brings the editor's busy-dependent state (for example its pickers' read-only flag) in line with
    /// <see cref="IsSaving"/>. Called when <see cref="IsSaving"/> changed, before its change notification; an editor
    /// may also call it when another input of that state changes (the task editor does when it is archived).
    /// </summary>
    protected virtual void OnSavingChanged()
    {
    }

    /// <summary>Called when <see cref="IsDirty"/> changed, before its change notification.</summary>
    protected virtual void OnDirtyChanged()
    {
    }

    /// <summary>Called when <see cref="IsDeletedElsewhere"/> changed, before its change notification.</summary>
    protected virtual void OnDeletedElsewhereChanged()
    {
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _commits?.Dispose();
        }
    }

    private void ClearErrors()
    {
        ClearFieldErrors();
        ValidationSummary = null;
        SaveError = null;
    }

    partial void OnIsSavingChanged(bool value) => OnSavingChanged();

    partial void OnIsDirtyChanged(bool value) => OnDirtyChanged();

    partial void OnIsDeletedElsewhereChanged(bool value) => OnDeletedElsewhereChanged();
}
