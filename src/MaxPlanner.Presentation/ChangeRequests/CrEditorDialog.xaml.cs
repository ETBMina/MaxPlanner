using System.ComponentModel;
using MaxPlanner.Presentation.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.ChangeRequests;

/// <summary>
/// The CR editor dialog. Code-behind only forwards clicks and keys to <see cref="CrEditorViewModel"/>, keeps
/// the combo box selections in step with it (a two-way SelectedItem could run before ItemsSource), moves
/// focus where the ViewModel asks, and reflows the field pairs by width (design §5.0).
/// </summary>
public sealed partial class CrEditorDialog : ContentDialog
{
    public CrEditorDialog(CrEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SquadCombo.SelectedItem = viewModel.SelectedSquad;
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        PriorityCombo.SelectedItem = viewModel.SelectedPriority;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Loaded += (_, _) => ApplyLayout();
        Opened += (_, _) => NumberBox.Focus(FocusState.Programmatic);
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

    public CrEditorViewModel ViewModel { get; }

    // Design §5.0 width rules: field pairs side by side only when the window leaves room; S or a short window
    // gets a full-size dialog so Save and Cancel stay reachable (T-08.1).
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
        Pair(NumberPair, TaskNumberPanel, twoColumns);
        Pair(OwnerPair, SquadPanel, twoColumns);
        Pair(StatusPair, PriorityPanel, twoColumns);
        Pair(PlannedPair, PlannedEndPanel, twoColumns);
    }

    private static void Pair(Microsoft.UI.Xaml.Controls.Grid pair, FrameworkElement second, bool twoColumns)
    {
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(second, twoColumns ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(second, twoColumns ? 0 : 1);
        pair.ColumnDefinitions[1].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ApplyLayout();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CrEditorViewModel.SelectedSquad) when !Equals(SquadCombo.SelectedItem, ViewModel.SelectedSquad):
                SquadCombo.SelectedItem = ViewModel.SelectedSquad;
                break;
            case nameof(CrEditorViewModel.SelectedStatus) when !Equals(StatusCombo.SelectedItem, ViewModel.SelectedStatus):
                StatusCombo.SelectedItem = ViewModel.SelectedStatus;
                break;
            case nameof(CrEditorViewModel.SelectedPriority) when !Equals(PriorityCombo.SelectedItem, ViewModel.SelectedPriority):
                PriorityCombo.SelectedItem = ViewModel.SelectedPriority;
                break;
            case nameof(CrEditorViewModel.CurrentStep) when ViewModel.IsDeleteStep:
                // Design §5.7: the destructive action is never the default; focus starts on Cancel.
                DispatcherQueue.TryEnqueue(() => DeleteBackButton.Focus(FocusState.Programmatic));
                break;
            default:
                break;
        }
    }

    private void OnFocusRequested(object? sender, string field)
    {
        // Design §5.0: focus moves to the first invalid field.
        Control? target = field switch
        {
            "Number" => NumberBox,
            "TaskNumber" => TaskNumberBox,
            "Title" => TitleBox,
            "Squad" => SquadCombo,
            "Status" => StatusCombo,
            "Priority" => PriorityCombo,
            "PlannedEnd" => PlannedEndPicker,
            "EmailSubject" => EmailSubjectBox,
            "SolutionDesignUrl" => UrlBox,
            "Description" => DescriptionBox,
            _ => null,
        };
        if (field == "Owner")
        {
            OwnerPicker.FocusBox();
            return;
        }

        if (field.StartsWith("Custom:", StringComparison.Ordinal))
        {
            var key = field["Custom:".Length..];
            DispatcherQueue.TryEnqueue(() => CustomFieldsPanel.FocusField(key));
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

        // Design §5.8: while the dirty-close confirmation is open it owns the decision, so the editor stays open.
        args.Cancel = ViewModel.IsDirtyClosePromptOpen || !ViewModel.TryClose();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsFormStep)
        {
            args.Handled = true;
            _ = ViewModel.SaveAsync();
        }
    }

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.IsFormStep)
        {
            args.Handled = true;
            ViewModel.StepBack();
        }
    }

    private void OnSquadSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedSquad = SquadCombo.SelectedItem as SquadOption;

    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as CrStatusOption;

    private void OnPrioritySelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedPriority = PriorityCombo.SelectedItem as PriorityOption;

    private void OnClearStartClick(object sender, RoutedEventArgs e) => ViewModel.ClearPlannedStart();

    private void OnClearEndClick(object sender, RoutedEventArgs e) => ViewModel.ClearPlannedEnd();

    private void OnOpenUrlClick(object sender, RoutedEventArgs e) => _ = ViewModel.OpenUrlAsync();

    private void OnOpenDuplicateClick(object sender, RoutedEventArgs e) => ViewModel.OpenDuplicate();

    private void OnCreateTaskClick(object sender, RoutedEventArgs e) => ViewModel.BeginCreateTask();

    private void OnSaveAndCreateClick(object sender, RoutedEventArgs e) => _ = ViewModel.SaveAndCreateTaskAsync();

    private void OnLinkedTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LinkedTaskRowViewModel row)
        {
            ViewModel.OpenLinkedTask(row);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.BeginDelete();

    private void OnMoveToBacklogClick(object sender, RoutedEventArgs e) => ViewModel.RequestMoveToBacklog();

    private void OnConfirmDeleteClick(object sender, RoutedEventArgs e) => _ = ViewModel.ConfirmDeleteAsync();

    private void OnReviewConflictClick(object sender, RoutedEventArgs e) => ViewModel.ReviewConflict();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ViewModel.BeginReload();

    private void OnConfirmReloadClick(object sender, RoutedEventArgs e) => ViewModel.ConfirmReload();

    private void OnApplyConflictClick(object sender, RoutedEventArgs e) => ViewModel.ApplyConflictReview();

    private void OnStepBackClick(object sender, RoutedEventArgs e) => ViewModel.StepBack();
}
