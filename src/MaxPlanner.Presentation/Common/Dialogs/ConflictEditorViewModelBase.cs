using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>Where an editor with conflict steps is: its form, or one of the two conflict steps (design §5.10).</summary>
public enum ConflictStep
{
    Form,
    Review,
    ReloadConfirm,
}

/// <summary>
/// An editor whose save can end in a revision conflict that the user resolves in place (T-05, B-02.9, design §5.10):
/// Review changes lists Field | Your draft | Saved now with Use mine, Apply to draft moves the draft to the saved
/// revision keeping the chosen values, and Reload replaces the draft after a confirmation. The task, CR, and Subtask
/// card editors use it; the review editor has no conflict steps and derives from <see cref="EditorViewModelBase{TFollowUp}"/>.
/// </summary>
/// <remarks>
/// Each editor keeps what is its own: its step enum and how a <see cref="ConflictStep"/> maps onto it
/// (<see cref="ShowConflictStep"/>), how the rows are built from its draft and the latest record
/// (<see cref="BuildConflictRows"/>), how an applied review updates its draft (<see cref="ApplyReviewToDraft"/>), what a
/// reload loads (<see cref="ReloadLatest"/>), and its Back.
/// </remarks>
/// <typeparam name="TFollowUp">What the editor asks for after it closes; its default value is "none".</typeparam>
public abstract partial class ConflictEditorViewModelBase<TFollowUp> : EditorViewModelBase<TFollowUp>
    where TFollowUp : struct, Enum
{
    private readonly ObservableCollection<ConflictRowViewModel> _conflictRows = [];

    /// <inheritdoc cref="EditorViewModelBase{TFollowUp}(WorkspaceContext, bool, string)"/>
    protected ConflictEditorViewModelBase(WorkspaceContext context, bool isNew, string recordKey)
        : base(context, isNew, recordKey)
    {
        ConflictRows = new ReadOnlyObservableCollection<ConflictRowViewModel>(_conflictRows);
    }

    /// <summary>The rows of the review step; empty outside it.</summary>
    public ReadOnlyObservableCollection<ConflictRowViewModel> ConflictRows { get; }

    /// <summary>Review changes (design §5.10): Field | Your draft | Saved now for each differing field.</summary>
    [RelayCommand]
    public void ReviewConflict()
    {
        if (!IsConflict || BuildConflictRows() is not { } rows)
        {
            return;
        }

        _conflictRows.Clear();
        foreach (var row in rows)
        {
            _conflictRows.Add(row);
        }

        ShowConflictStep(ConflictStep.Review);
        RaiseState();
    }

    /// <summary>
    /// Apply to draft: the draft moves to the saved revision, keeping the user's value where Use mine is on and
    /// taking the saved value elsewhere. Nothing is saved until the user saves again.
    /// </summary>
    [RelayCommand]
    public void ApplyConflictReview()
    {
        var keep = _conflictRows.Where(r => r.UseMine).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        if (!ApplyReviewToDraft(keep))
        {
            return;
        }

        EndConflict();
        _conflictRows.Clear();
        ShowConflictStep(ConflictStep.Form);
        RecomputeDirty();
        RaiseState();
    }

    /// <summary>Reload asks first, because it discards the draft (design §5.10).</summary>
    [RelayCommand]
    public void BeginReload()
    {
        ShowConflictStep(ConflictStep.ReloadConfirm);
        RaiseState();
    }

    /// <summary>Reload confirmed: the draft is replaced by the latest record and the conflict ends.</summary>
    [RelayCommand]
    public void ConfirmReload()
    {
        ReloadLatest();
        EndConflict();
        ShowConflictStep(ConflictStep.Form);
        RaiseState();
    }

    /// <summary>The rows for the fields where the draft differs from the latest record.</summary>
    /// <returns>The rows, or null when there is no latest record to compare with (the review does not open).</returns>
    protected abstract IReadOnlyList<ConflictRowViewModel>? BuildConflictRows();

    /// <summary>
    /// Loads the latest record into the draft, then restores the draft's own values for the fields in
    /// <paramref name="keep"/>. Dirty tracking is recomputed afterwards.
    /// </summary>
    /// <param name="keep">The keys of the rows whose Use mine is on.</param>
    /// <returns>False when there is no latest record; nothing changes then.</returns>
    protected abstract bool ApplyReviewToDraft(IReadOnlySet<string> keep);

    /// <summary>Replaces the draft with the latest record, if there is one.</summary>
    protected abstract void ReloadLatest();

    /// <summary>Moves the editor to its own step for <paramref name="conflictStep"/>; the caller raises the state afterwards.</summary>
    protected abstract void ShowConflictStep(ConflictStep conflictStep);
}
