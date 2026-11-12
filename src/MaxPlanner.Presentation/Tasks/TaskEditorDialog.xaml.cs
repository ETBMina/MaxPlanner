using System.ComponentModel;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using XamlGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>
/// The task editor dialog. Buttons and menu items bind <see cref="TaskEditorViewModel"/> commands; code-behind
/// handles what needs event args or the row's item (the dialog buttons, accelerators, Enter keys, checklist rows),
/// keeps the Status/Priority/CR selections in step with it (a two-way SelectedItem could run before
/// ItemsSource), moves focus where the ViewModel asks, reflows the fields by width (design §5.0), shows the
/// hover-revealed copy and checklist row actions, and turns a checklist drag into a draft move.
/// </summary>
public sealed partial class TaskEditorDialog : ContentDialog
{
    // Elements marked Tag="Reveal" show while the pointer is over their host or focus is inside it.
    private const string RevealTag = "Reveal";

    private readonly HashSet<FrameworkElement> _pointerOverHosts = [];
    private EditorChecklistItemViewModel? _draggedItem;

    public TaskEditorDialog(TaskEditorViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        EditorDialogLayout.Attach(this, ApplyLayout);
        StatusCombo.SelectedItem = viewModel.SelectedStatus;
        PriorityCombo.SelectedItem = viewModel.SelectedPriority;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.FocusRequested += OnFocusRequested;
        Unloaded += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.FocusRequested -= OnFocusRequested;
        };
    }

    public TaskEditorViewModel ViewModel { get; }

    /// <summary>The AutomationId of a conflict row's Use mine box (design §8.1: stable keys, never display text).</summary>
    public static string UseMineId(string key) => "Conflict_UseMineCheck_" + key;

    public static string CrName(string number, string title, string secondary) => number + ", " + title + ", " + secondary;

    // Design §5.0 width rules: the fields sit side by side only when the window leaves room, and stack in one column
    // otherwise (the facts strip keeps two). S or a short window gets a full-size dialog so Save and Cancel stay
    // reachable (T-08.1).
    private void ApplyLayout(Windows.Foundation.Size size)
    {
        var wide = size.Width >= EditorDialogSizing.LargeBreakpoint;
        Reflow(FactsGrid, wide ? 4 : 2);
        Reflow(WorkflowFields, wide ? 3 : 1);
        Reflow(PeopleFields, wide ? 2 : 1);
        Reflow(DateFields, wide ? 2 : 1);
    }

    // Places the grid's children in reading order across the given number of equal columns.
    private static void Reflow(XamlGrid grid, int columns)
    {
        if (grid.ColumnDefinitions.Count == columns)
        {
            return;
        }

        grid.ColumnDefinitions.Clear();
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        grid.RowDefinitions.Clear();
        for (var i = 0; i < (grid.Children.Count + columns - 1) / columns; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var i = 0; i < grid.Children.Count; i++)
        {
            XamlGrid.SetColumn((FrameworkElement)grid.Children[i], i % columns);
            XamlGrid.SetRow((FrameworkElement)grid.Children[i], i / columns);
        }
    }

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
        // Design §5.0: focus moves to the first invalid field.
        Control? target = field switch
        {
            "Title" => TitleBox,
            "Service" => ServiceBox,
            "Status" => StatusCombo,
            "Priority" => PriorityCombo,
            "EndDate" => EndDatePicker,
            "Notes" => NotesBox,
            "ChecklistItems" => ChecklistList,
            _ => null,
        };
        var picker = field switch
        {
            "Designer" => DesignerPicker,
            "Developer" => DeveloperPicker,
            "Tags" => TagsPicker,
            _ => null,
        };
        if (picker is not null)
        {
            picker.FocusBox();
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

    // The dialog would focus its first control, the tag field, which then looks active while the user has not touched
    // it. An editable title takes focus instead, as in Planner; otherwise the Close button, which shows no focus
    // visual for a programmatic focus.
    // The dialog places its own initial focus after Opened, so this runs at low priority, once that has happened.
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Control target = ViewModel.IsTitleEditable ? TitleBox : CloseIconButton;
            target.Focus(FocusState.Programmatic);
        });

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

    // Hide raises Closing with no result, so a dirty draft asks first, as Cancel and Esc do (design §5.8).
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    // The plain numbers, not the configured Copy ID format.
    private void OnCopyCrNumberClick(object sender, RoutedEventArgs e) => ClipboardText.Set(ViewModel.CrNumberToCopy);

    private void OnCopyTaskNumberClick(object sender, RoutedEventArgs e) => ClipboardText.Set(ViewModel.TaskNumberToCopy);

    private void OnRevealPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
        {
            _pointerOverHosts.Add(host);
            UpdateReveal(host);
        }
    }

    private void OnRevealPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
        {
            _pointerOverHosts.Remove(host);
            UpdateReveal(host);
        }
    }

    // Focus moving between a host's own parts raises LostFocus too, so the state is read once focus has settled.
    private void OnRevealFocusChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
        {
            DispatcherQueue.TryEnqueue(() => UpdateReveal(host));
        }
    }

    // Opacity, not Visibility: the hidden parts keep their space, so nothing shifts, and stay reachable by Tab.
    private void UpdateReveal(FrameworkElement host)
    {
        var shown = _pointerOverHosts.Contains(host) || IsFocusWithin(host);
        SetRevealOpacity(host, shown ? 1 : 0);
    }

    private static void SetRevealOpacity(DependencyObject parent, double opacity)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Tag: RevealTag } element)
            {
                element.Opacity = opacity;
            }
            else
            {
                SetRevealOpacity(child, opacity);
            }
        }
    }

    private bool IsFocusWithin(DependencyObject host)
    {
        var element = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, host))
            {
                return true;
            }
        }

        return false;
    }

    private void OnSubtaskCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EditorSubtaskCardViewModel card)
        {
            ViewModel.OpenSubtaskCard(card);
        }
    }

    // Not a two-way SelectedItem: that could write null back while ItemsSource is (re)set.
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

    private void OnAddChecklistKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.AddChecklistItem();
        }
    }

    private void OnAddSubtaskKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.AddSubtaskCardCommand.Execute(null);
        }
    }

    private void OnItemMoveUpClick(object sender, RoutedEventArgs e) => MoveItem(sender, -1);

    private void OnItemMoveDownClick(object sender, RoutedEventArgs e) => MoveItem(sender, 1);

    private void OnItemPopOutClick(object sender, RoutedEventArgs e) => WithItem(sender, ViewModel.PopOutCommand.Execute);

    private void OnItemDeleteClick(object sender, RoutedEventArgs e) => WithItem(sender, ViewModel.RemoveChecklistItem);

    // The list moves the row's container, so the pressed button keeps focus in its new place for repeated presses.
    private void MoveItem(object sender, int delta)
    {
        if (sender is not Control { DataContext: EditorChecklistItemViewModel item } button)
        {
            return;
        }

        var automationId = AutomationProperties.GetAutomationId(button);
        var focusState = button.FocusState;
        ViewModel.MoveChecklistItem(item, delta);
        if (focusState != FocusState.Unfocused)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ChecklistList.ContainerFromItem(item) is DependencyObject row && FindByAutomationId(row, automationId) is { } moved)
                {
                    moved.Focus(focusState);
                }
            });
        }
    }

    private static Control? FindByAutomationId(DependencyObject parent, string automationId)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Control control && AutomationProperties.GetAutomationId(control) == automationId)
            {
                return control;
            }

            if (FindByAutomationId(child, automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // A drag reorders the draft (B-02.7): the list only reports where the item was dropped.
    private void OnChecklistDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedItem = ViewModel.CanEditFields ? e.Items.FirstOrDefault() as EditorChecklistItemViewModel : null;
        e.Cancel = _draggedItem is null;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnChecklistDragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = _draggedItem is null ? DataPackageOperation.None : DataPackageOperation.Move;

    private void OnChecklistDrop(object sender, DragEventArgs e)
    {
        if (_draggedItem is not { } item)
        {
            return;
        }

        // The drop goes before the first row whose middle is below the pointer; only realized rows are measured.
        var from = ViewModel.ChecklistItems.IndexOf(item);
        var y = e.GetPosition(ChecklistList).Y;
        var insertAt = from;
        for (var i = 0; i < ViewModel.ChecklistItems.Count; i++)
        {
            if (ChecklistList.ContainerFromIndex(i) is not FrameworkElement row)
            {
                continue;
            }

            var top = row.TransformToVisual(ChecklistList).TransformPoint(default).Y;
            if (y < top + (row.ActualHeight / 2))
            {
                insertAt = i;
                break;
            }

            insertAt = i + 1;
        }

        ViewModel.MoveChecklistItemTo(item, insertAt > from ? insertAt - 1 : insertAt);
        _draggedItem = null;
    }

    // A row removed or moved under the pointer gets no PointerExited, so a reused container starts hidden again.
    private void OnChecklistContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is FrameworkElement host)
        {
            _pointerOverHosts.Remove(host);
            SetRevealOpacity(host, 0);
        }
    }

    private void OnChecklistDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggedItem = null;

    private static void WithItem(object sender, Action<EditorChecklistItemViewModel> action)
    {
        if (sender is FrameworkElement { DataContext: EditorChecklistItemViewModel item })
        {
            action(item);
        }
    }
}
