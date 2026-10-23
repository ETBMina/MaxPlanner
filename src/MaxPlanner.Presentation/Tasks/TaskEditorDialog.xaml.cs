using System.ComponentModel;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>
/// The task editor dialog. Code-behind only forwards clicks and keys to <see cref="TaskEditorViewModel"/>,
/// keeps the Status/Priority/CR selections in step with it (a two-way SelectedItem could run before
/// ItemsSource), moves focus where the ViewModel asks, and reflows the fields by width (design §5.0).
/// </summary>
public sealed partial class TaskEditorDialog : ContentDialog
{
    public TaskEditorDialog(TaskEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        PriorityCombo.SelectedItem = viewModel.SelectedPriority;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Loaded += (_, _) => ApplyLayout();
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

    public TaskEditorViewModel ViewModel { get; }

    /// <summary>The AutomationId of a conflict row's Use mine box (design §8.1: stable keys, never display text).</summary>
    public static string UseMineId(string key) => "Conflict_UseMineCheck_" + key;

    public static string CrName(string number, string title, string secondary) => number + ", " + title + ", " + secondary;

    // Design §5.0 width rules: two field columns only when the window leaves room; S or a short window gets a
    // full-size dialog so Save and Cancel stay reachable (T-08.1).
    private void ApplyLayout()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        root.Changed -= OnXamlRootChanged;
        root.Changed += OnXamlRootChanged;
        var size = root.Size;
        FullSizeDesired = size.Width < 640 || size.Height < 600;
        var twoColumns = size.Width >= 1008;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(RightPanel, twoColumns ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(RightPanel, twoColumns ? 0 : 1);
        RightColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyLayout();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TaskEditorViewModel.SelectedStatus) when !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus):
                StatusCombo.SelectedItem = ViewModel.SelectedStatus;
                break;
            case nameof(TaskEditorViewModel.SelectedPriority) when !Equals(PriorityCombo.SelectedItem, ViewModel.SelectedPriority):
                PriorityCombo.SelectedItem = ViewModel.SelectedPriority;
                break;
            case nameof(TaskEditorViewModel.IsLinkPickStep) when ViewModel.IsLinkPickStep:
                CrResultsList.SelectedItem = ViewModel.CrPicker.Selected;
                DispatcherQueue.TryEnqueue(() => CrSearchBox.Focus(FocusState.Programmatic));
                break;
            default:
                break;
        }
    }

    private void OnFocusRequested(object? sender, string field)
    {
        // Design §5.0: focus moves to the first invalid field; §5.8: to Save on the dirty-close prompt.
        Control? target = field switch
        {
            "Title" => TitleBox,
            "Service" => ServiceBox,
            "Status" => StatusCombo,
            "Priority" => PriorityCombo,
            "EndDate" => EndDatePicker,
            "Notes" => NotesBox,
            "ChecklistItems" => ChecklistList,
            "DirtyClose" => DirtyCloseSaveButton,
            _ => null,
        };
        if (field == "Designers")
        {
            DesignersPicker.FocusBox();
            return;
        }

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

    private void OnCopyClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(EditorFollowUp.Copy);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(EditorFollowUp.Delete);

    private void OnArchiveClick(object sender, RoutedEventArgs e) => _ = ViewModel.ArchiveAsync();

    private void OnRestoreClick(object sender, RoutedEventArgs e) => _ = ViewModel.RestoreAsync();

    private void OnAddSubtaskCardFollowUpClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(EditorFollowUp.AddSubtaskCard);

    private void OnOpenCrClick(object sender, RoutedEventArgs e) => ViewModel.RequestFollowUp(EditorFollowUp.OpenCr);

    private void OnSubtaskCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EditorSubtaskCardViewModel card)
        {
            ViewModel.OpenSubtaskCard(card);
        }
    }

    private void OnDirtySaveClick(object sender, RoutedEventArgs e) => _ = ViewModel.SaveFromPromptAsync();

    private void OnDiscardClick(object sender, RoutedEventArgs e) => ViewModel.DiscardChanges();

    private void OnContinueClick(object sender, RoutedEventArgs e) => ViewModel.ContinueEditing();

    private void OnReviewConflictClick(object sender, RoutedEventArgs e) => ViewModel.ReviewConflict();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ViewModel.BeginReload();

    private void OnConfirmReloadClick(object sender, RoutedEventArgs e) => ViewModel.ConfirmReload();

    private void OnApplyConflictClick(object sender, RoutedEventArgs e) => ViewModel.ApplyConflictReview();

    private void OnLinkClick(object sender, RoutedEventArgs e) => ViewModel.BeginLink();

    private void OnLinkNextClick(object sender, RoutedEventArgs e) => ViewModel.LinkNext();

    private void OnConfirmLinkClick(object sender, RoutedEventArgs e) => ViewModel.ConfirmLink();

    private void OnStepBackClick(object sender, RoutedEventArgs e) => ViewModel.StepBack();

    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as StatusOption;

    private void OnPrioritySelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedPriority = PriorityCombo.SelectedItem as PriorityOption;

    private void OnCrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The picker refreshes its results while typing; a transient null selection keeps the chosen CR.
        if (CrResultsList.SelectedItem is CrChoice choice)
        {
            ViewModel.CrPicker.Selected = choice;
        }
    }

    private void OnClearStartClick(object sender, RoutedEventArgs e) => ViewModel.ClearStartDate();

    private void OnClearEndClick(object sender, RoutedEventArgs e) => ViewModel.ClearEndDate();

    private void OnAddChecklistClick(object sender, RoutedEventArgs e) => ViewModel.AddChecklistItem();

    private void OnAddChecklistKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.AddChecklistItem();
        }
    }

    private void OnAddSubtaskClick(object sender, RoutedEventArgs e) => _ = ViewModel.AddSubtaskCardAsync();

    private void OnAddSubtaskKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = ViewModel.AddSubtaskCardAsync();
        }
    }

    private void OnChecklistMenuOpening(object sender, object e)
    {
        // Pop out runs immediately against the saved task, so it is off for a dirty draft or a new item.
        // Flyout items are outside the visual tree, so they get the row's item explicitly.
        if (sender is MenuFlyout menu && menu.Target?.DataContext is EditorChecklistItemViewModel item)
        {
            foreach (var entry in menu.Items.OfType<MenuFlyoutItem>())
            {
                entry.DataContext = item;
                if (entry.Tag as string == "PopOut")
                {
                    entry.IsEnabled = ViewModel.CanPopOut && !item.IsNew;
                }
            }
        }
    }

    private void OnItemMoveUpClick(object sender, RoutedEventArgs e) => WithItem(sender, item => ViewModel.MoveChecklistItem(item, -1));

    private void OnItemMoveDownClick(object sender, RoutedEventArgs e) => WithItem(sender, item => ViewModel.MoveChecklistItem(item, 1));

    private void OnItemPopOutClick(object sender, RoutedEventArgs e) => WithItem(sender, item => _ = ViewModel.PopOutAsync(item));

    private void OnItemDeleteClick(object sender, RoutedEventArgs e) => WithItem(sender, ViewModel.RemoveChecklistItem);

    private static void WithItem(object sender, Action<EditorChecklistItemViewModel> action)
    {
        if (sender is FrameworkElement { DataContext: EditorChecklistItemViewModel item })
        {
            action(item);
        }
    }
}
