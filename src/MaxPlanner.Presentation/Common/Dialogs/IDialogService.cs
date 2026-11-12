namespace MaxPlanner.Presentation.Common.Dialogs;

/// <summary>Shows view-model dialogs (design §5), one at a time. Use on the UI thread.</summary>
public interface IDialogService
{
    /// <summary>Shows the dialog and completes when it closed. Without a dialog host (no window) nothing is shown.</summary>
    Task ShowDialogAsync(IDialogViewModel viewModel);
}
