using System.ComponentModel;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.SubtaskCards;

/// <summary>
/// The Subtask card editor dialog. Buttons bind commands; code-behind forwards the dialog buttons and keys to
/// <see cref="SubtaskCardEditorViewModel"/>, keeps the Status selection in step with it, moves focus where
/// the ViewModel asks, and uses a full-size dialog in small or short windows (design §5.0).
/// </summary>
public sealed partial class SubtaskCardEditorDialog : ContentDialog
{
    public SubtaskCardEditorDialog(SubtaskCardEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogLayout.Attach(this);
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Opened += (_, _) => SubtitleBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.FocusRequested -= OnFocusRequested;
        };
    }

    public SubtaskCardEditorViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtaskCardEditorViewModel.SelectedStatus) && !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus))
        {
            StatusCombo.SelectedItem = ViewModel.SelectedStatus;
        }
    }

    private void OnFocusRequested(object? sender, string field)
    {
        // Design §5.0: focus moves to the first invalid field.
        Control? target = field switch
        {
            "Subtitle" => SubtitleBox,
            "Status" => StatusCombo,
            "EndDate" => EndDatePicker,
            "Notes" => NotesBox,
            _ => null,
        };
        if (target is not null)
        {
            DispatcherQueue.TryEnqueue(() => target.Focus(FocusState.Programmatic));
        }
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // The dialog closes only after the save committed (the ViewModel asks for it); a failure keeps it open.
        args.Cancel = true;
        ViewModel.SaveCommand.Execute(null);
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.None)
        {
            return;
        }

        // Design §5.8: while the dirty-close confirmation is open it owns the decision, so the editor stays open.
        args.Cancel = ViewModel.IsDirtyClosePromptOpen || !ViewModel.TryClose();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SaveCommand.Execute(null);
    }

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsFormStep)
        {
            args.Handled = true;
            ViewModel.StepBack();
        }
    }

    // Not a two-way SelectedItem: that could write null back while ItemsSource is (re)set.
    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as StatusOption;
}
