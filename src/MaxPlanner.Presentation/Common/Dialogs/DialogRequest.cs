using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>A dialog's ViewModel. It asks its host to close it, for example after a successful save.</summary>
public interface IDialogViewModel
{
    event EventHandler? CloseRequested;
}

/// <summary>
/// An editor whose unsaved draft must survive a main-window close (design §5.8): closing the window while it is
/// open asks the same Save / Discard / Continue editing question as Esc does, and the window closes only after
/// the user chose Save (and it succeeded) or Discard.
/// </summary>
public interface IWindowCloseGuard
{
    /// <summary>
    /// True from a refused <see cref="TryCloseForWindow"/> until the prompt is answered with Continue editing or a
    /// failed save. While it stays true, the dialog closing means the window may close too.
    /// </summary>
    bool IsWindowClosePending { get; }

    /// <summary>Called on the UI thread when the main window is asked to close while this editor is open.</summary>
    /// <returns>True when nothing would be lost and the window may close now; false when the dirty prompt is shown.</returns>
    bool TryCloseForWindow();
}

/// <summary>A dialog whose closing by Esc, Cancel or an outside click is ignored while its command runs (design §5.0).</summary>
public interface IBusyDialog
{
    bool IsSaving { get; }
}

/// <summary>
/// An editor that holds a draft (design §5.8). A refused <see cref="TryClose"/> sets
/// <see cref="IsDirtyClosePromptOpen"/>; the dialog host then shows the Save / Discard / Continue editing
/// confirmation and answers with exactly one of <see cref="SaveFromPromptAsync"/>, <see cref="DiscardChanges"/>
/// or <see cref="ContinueEditing"/>. Property changes are raised on the UI thread.
/// </summary>
public interface IDirtyCloseEditor : IBusyDialog, IWindowCloseGuard, INotifyPropertyChanged
{
    bool IsDirtyClosePromptOpen { get; }

    /// <summary>The dialog is closing by Esc, Cancel, an outside click or a follow-up command.</summary>
    /// <returns>True when the dialog may close; false while saving or when the confirmation is needed.</returns>
    bool TryClose();

    /// <summary>Runs the normal Save; the editor closes only when it succeeds and otherwise shows its errors.</summary>
    Task<bool> SaveFromPromptAsync();

    void DiscardChanges();

    void ContinueEditing();
}

/// <summary>One dialog to show (design §5.0: dialogs are serialized and never stack).</summary>
public sealed class DialogRequest
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogRequest(IDialogViewModel viewModel) => ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    public IDialogViewModel ViewModel { get; }

    /// <summary>Completes when the host has closed the dialog.</summary>
    public Task Closed => _closed.Task;

    public void MarkClosed() => _closed.TrySetResult();
}

public enum ConfirmationResult
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>
/// A confirmation with a verb-labelled primary action and an optional alternative (design §5.7). Cancel
/// is the default button, so Enter never confirms a destructive action by accident.
/// </summary>
public sealed partial class ConfirmationViewModel : ObservableObject, IDialogViewModel
{
    public ConfirmationViewModel(string automationId, string title, string message, string primaryText, string? secondaryText, string closeText)
    {
        AutomationId = automationId;
        Title = title;
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        CloseText = closeText;
    }

    public event EventHandler? CloseRequested;

    /// <summary>The dialog's AutomationId, for example <c>DeleteTask_Dialog</c> (design §8.1).</summary>
    public string AutomationId { get; }

    public string Title { get; }

    public string Message { get; }

    public string PrimaryText { get; }

    public string? SecondaryText { get; }

    public string CloseText { get; }

    /// <summary>Set by the host (or a test) before the dialog closes.</summary>
    public ConfirmationResult Result { get; set; }

    /// <summary>Closes the dialog with <paramref name="result"/>; used by tests in place of a click.</summary>
    public void Choose(ConfirmationResult result)
    {
        Result = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
