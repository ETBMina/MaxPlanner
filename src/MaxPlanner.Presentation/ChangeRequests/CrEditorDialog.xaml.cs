using System.ComponentModel;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.ChangeRequests;

/// <summary>
/// The CR editor dialog. Buttons bind <see cref="CrEditorViewModel"/> commands; code-behind forwards the dialog
/// buttons and keys to it, keeps the combo box selections in step with it (a two-way SelectedItem could run before ItemsSource), moves
/// focus where the ViewModel asks, and reflows the field pairs by width (design §5.0).
/// </summary>
public sealed partial class CrEditorDialog : ContentDialog
{
    public CrEditorDialog(CrEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogLayout.Attach(this, ApplyLayout);
        SquadCombo.SelectedItem = viewModel.SelectedSquad;
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        PriorityCombo.SelectedItem = viewModel.SelectedPriority;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Opened += (_, _) => NumberBox.Focus(FocusState.Programmatic);
        Unloaded += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.FocusRequested -= OnFocusRequested;
        };
    }

    public CrEditorViewModel ViewModel { get; }

    // Design §5.0 width rules: field pairs side by side only when the window leaves room; S or a short window
    // gets a full-size dialog so Save and Cancel stay reachable (T-08.1).
    private void ApplyLayout(Windows.Foundation.Size size)
    {
        var twoColumns = size.Width >= EditorDialogSizing.LargeBreakpoint;
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
        if (ViewModel.IsFormStep)
        {
            args.Handled = true;
            ViewModel.SaveCommand.Execute(null);
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

    // Not a two-way SelectedItem: that could write null back while ItemsSource is (re)set.
    private void OnSquadSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedSquad = SquadCombo.SelectedItem as SquadOption;

    private void OnStatusSelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedStatus = StatusCombo.SelectedItem as CrStatusOption;

    private void OnPrioritySelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel.SelectedPriority = PriorityCombo.SelectedItem as PriorityOption;

    private void OnLinkedTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LinkedTaskRowViewModel row)
        {
            ViewModel.OpenLinkedTask(row);
        }
    }
}
