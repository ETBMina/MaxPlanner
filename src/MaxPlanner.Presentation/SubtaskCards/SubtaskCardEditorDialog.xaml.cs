using System.ComponentModel;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.SubtaskCards;

/// <summary>
/// The Subtask card editor dialog. Code-behind only forwards clicks and keys to
/// <see cref="SubtaskCardEditorViewModel"/>, keeps the Status selection in step with it, moves focus where
/// the ViewModel asks, and uses a full-size dialog in small or short windows (design §5.0).
/// </summary>
public sealed partial class SubtaskCardEditorDialog : ContentDialog
{
    public SubtaskCardEditorDialog(SubtaskCardEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Loaded += (_, _) => ApplyLayout();
        Opened += (_, _) => SubtitleBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.FocusRequested -= OnFocusRequested;
            if (XamlRoot is not null)
            {
                XamlRoot.Changed -= OnXamlRootChanged;
            }
        };
    }

    public SubtaskCardEditorViewModel ViewModel { get; }

    private void ApplyLayout()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        root.Changed -= OnXamlRootChanged;
        root.Changed += OnXamlRootChanged;
        FullSizeDesired = root.Size.Width < 640 || root.Size.Height < 600;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyLayout();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtaskCardEditorViewModel.SelectedStatus) && !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus))
        {
            StatusCombo.SelectedItem = ViewModel.SelectedStatus;
        }
    }

    private void OnFocusRequested(object? sender, string field)
    {
        // Design §5.0: focus moves to the first invalid field; §5.8: to Save on the dirty-close prompt.
        Control? target = field switch
        {
            "Subtitle" => SubtitleBox,
            "Status" => StatusCombo,
            "EndDate" => EndDatePicker,
            "Notes" => NotesBox,
            "DirtyClose" => DirtyCloseSaveButton,
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
        _ = ViewModel.SaveAsync();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.None)
        {
            return;
        }

        // Design §5.8: Esc on the prompt means Continue editing.
        if (ViewModel.IsDirtyClosePromptOpen)
        {
            ViewModel.ContinueEditing();
            args.Cancel = true;
            return;
        }

        args.Cancel = !ViewModel.TryClose();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ViewModel.SaveAsync();
    }

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsFormStep)
        {
            args.Handled = true;
            ViewModel.StepBack();
        }
    }

    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as StatusOption;

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(SubtaskEditorFollowUp.Delete);

    private void OnOpenParentClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(SubtaskEditorFollowUp.OpenParent);

    private void OnClearStartClick(object sender, RoutedEventArgs e) => ViewModel.ClearStartDate();

    private void OnClearEndClick(object sender, RoutedEventArgs e) => ViewModel.ClearEndDate();

    private void OnDirtySaveClick(object sender, RoutedEventArgs e) => _ = ViewModel.SaveFromPromptAsync();

    private void OnDiscardClick(object sender, RoutedEventArgs e) => ViewModel.DiscardChanges();

    private void OnContinueClick(object sender, RoutedEventArgs e) => ViewModel.ContinueEditing();

    private void OnReviewConflictClick(object sender, RoutedEventArgs e) => ViewModel.ReviewConflict();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ViewModel.BeginReload();

    private void OnConfirmReloadClick(object sender, RoutedEventArgs e) => ViewModel.ConfirmReload();

    private void OnApplyConflictClick(object sender, RoutedEventArgs e) => ViewModel.ApplyConflictReview();

    private void OnStepBackClick(object sender, RoutedEventArgs e) => ViewModel.StepBack();
}
