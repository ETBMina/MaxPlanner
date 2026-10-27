using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Text;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>
/// The Checklists page view. Code-behind handles view mechanics only: forwarding checkbox and key events to the
/// row ViewModels, inline rename focus, the delete confirmation flyouts, the task picker, focusing an Add box, and
/// the breakpoint padding (design §4.5, §7.6).
/// </summary>
public sealed partial class ChecklistsPageView : UserControl, Shell.IPageView
{
    // The strings of the code-built confirmation flyout.
    private static readonly ResourceStrings Strings = new();

    // The Reviews card's key in IndexOfCard (it has no Add box focus target from the page commands).
    private const string ReviewsCardKey = "Reviews";

    private FrameworkElement? _menuAnchor;

    public ChecklistsPageView(ChecklistsPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        viewModel.FocusAddBoxRequested += OnFocusAddBoxRequested;
        SizeChanged += (_, e) =>
        {
            // A collapsed (hidden) page has no width to adapt to; it applies the real width when shown again.
            if (e.NewSize.Width > 0)
            {
                ApplyBreakpoint(e.NewSize.Width);
            }
        };
    }

    public ChecklistsPageViewModel ViewModel { get; }

    /// <summary>Completed rows are struck through as well as listed under Completed (P3: never color alone).</summary>
    public static TextDecorations Strike(bool isDone) => isDone ? TextDecorations.Strikethrough : TextDecorations.None;

    /// <summary>Secondary text for done items through a style, so it follows the window theme and High Contrast.</summary>
    public static Style ItemTextStyle(bool isDone) =>
        (Style)Microsoft.UI.Xaml.Application.Current.Resources[isDone ? "ChecklistDoneItemTextStyle" : "ChecklistItemTextStyle"];

    /// <inheritdoc/>
    public FrameworkElement HeaderElement => HeaderRoot;

    /// <inheritdoc/>
    public FrameworkElement? ContentElement => PageContent;

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    /// <inheritdoc/>
    public bool FocusSearch()
    {
        SearchBox.FocusSearch();
        return true;
    }

    /// <inheritdoc/>
    public void OnShown()
    {
        PageContent.OnPageShown();
        if (ViewModel.TakeRevealReviews())
        {
            BringCardIntoView(ReviewsCardKey, focusAutomationId: null);
        }
    }

    /// <inheritdoc/>
    public void OnHidden()
    {
        PageContent.OnPageHidden();
        ViewModel.OnPageLeft();
    }

    // Click rather than Checked/Unchecked: Click is raised only by user input, so the IsChecked binding updating a
    // recycled container never sends a command. The box shows the new value until the commit arrives; a failed save
    // puts it back (RestoreCommitted).
    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ChecklistItemViewModel item } box && box.IsChecked != item.IsChecked)
        {
            _ = item.ToggleAsync(box.IsChecked == true);
        }
    }

    private void OnReviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ReviewRowViewModel row } box && box.IsChecked != row.IsDone)
        {
            _ = row.ToggleAsync(box.IsChecked == true);
        }
    }

    // Design §7.6: F2 rename, Alt+Shift+Up/Down reorder, Ctrl+Shift+P pop out, Delete (flyout confirmation).
    private void OnItemKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ChecklistItemViewModel item } box)
        {
            return;
        }

        var alt = IsDown(VirtualKey.Menu);
        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);
        switch (e.Key)
        {
            case VirtualKey.F2:
                e.Handled = true;
                BeginRename(box, item);
                break;
            case VirtualKey.Up or VirtualKey.Down when alt && shift:
                e.Handled = true;
                var command = e.Key == VirtualKey.Up ? item.MoveUpCommand : item.MoveDownCommand;
                if (command.CanExecute(null))
                {
                    _ = MoveAndRefocusAsync(box, item, command);
                }

                break;
            case VirtualKey.P when ctrl && shift && item.CanPopOut:
                e.Handled = true;
                item.PopOutCommand.Execute(null);
                break;
            case VirtualKey.Delete:
                e.Handled = true;
                ShowDeleteFlyout(box, item.DeletePrompt, item.DeleteAsync);
                break;
            default:
                break;
        }
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ChecklistItemViewModel item } box)
        {
            e.Handled = true;
            BeginRename(box, item);
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ChecklistItemViewModel item } && _menuAnchor?.Parent is Panel row)
        {
            BeginRename(row.Children.OfType<CheckBox>().FirstOrDefault(), item);
        }
    }

    private void BeginRename(CheckBox? box, ChecklistItemViewModel item)
    {
        item.BeginRename();
        if (box?.Parent is Panel row && row.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBox>().FirstOrDefault() is { } editor)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                editor.Focus(FocusState.Keyboard);
                editor.SelectAll();
            });
        }
    }

    private async void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: ChecklistItemViewModel item } editor)
        {
            return;
        }

        try
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                if (await item.CommitRenameAsync().ConfigureAwait(true))
                {
                    FocusRowCheckBox(editor);
                }
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                item.CancelRename();
                FocusRowCheckBox(editor);
            }
        }
        catch (InvalidOperationException ex)
        {
            // A command failure is reported by the ViewModel; this guards the event handler (AGENTS.md §4).
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        // Leaving the box keeps the draft only while it shows an error; otherwise an unchanged rename closes.
        if (sender is TextBox { Tag: ChecklistItemViewModel item } && item.IsRenaming && !item.IsPending && item.RenameText == item.Text)
        {
            item.CancelRename();
        }
    }

    private void FocusRowCheckBox(FrameworkElement editor)
    {
        if (editor.Parent is FrameworkElement { Parent: Panel row } && row.Children.OfType<CheckBox>().FirstOrDefault() is { } box)
        {
            DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Keyboard));
        }
    }

    private static async Task MoveAndRefocusAsync(Control box, ChecklistItemViewModel item, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
    {
        await command.ExecuteAsync(null).ConfigureAwait(true);

        // Design §7.7: focus stays on the moved item in its new position (the row keeps its ViewModel).
        if (box.XamlRoot is not null && item.IsChecked == (box as CheckBox)?.IsChecked)
        {
            box.Focus(FocusState.Keyboard);
        }
    }

    private void OnItemMenuOpening(object? sender, object e) => _menuAnchor = (sender as FlyoutBase)?.Target;

    private void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ChecklistItemViewModel item } && _menuAnchor is { } anchor)
        {
            ShowDeleteFlyout(anchor, item.DeletePrompt, item.DeleteAsync);
        }
    }

    private void OnReviewDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ReviewRowViewModel row } && _menuAnchor is { } anchor)
        {
            ShowDeleteFlyout(anchor, row.DeletePrompt, row.DeleteAsync);
        }
    }

    private void OnReviewEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ReviewRowViewModel row })
        {
            _ = row.EditAsync();
        }
    }

    // Tapping a review's text opens View/Edit, as the ⋯ menu does.
    private void OnReviewTextTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReviewRowViewModel row })
        {
            e.Handled = true;
            _ = row.EditAsync();
        }
    }

    // Design §7.6 on a focused review checkbox: Enter = View/Edit, Delete asks in the light flyout,
    // Alt+Shift+Up/Down reorders open reviews.
    private void OnReviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ReviewRowViewModel row } box)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = row.EditAsync();
        }
        else if (e.Key == VirtualKey.Delete)
        {
            e.Handled = true;
            ShowDeleteFlyout(box, row.DeletePrompt, row.DeleteAsync);
        }
        else if (IsDown(VirtualKey.Menu) && IsDown(VirtualKey.Shift) && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            e.Handled = true;
            (e.Key == VirtualKey.Up ? row.MoveUpCommand : row.MoveDownCommand).Execute(null);
        }
    }

    /// <summary>Design §4.5: "Delete 'Draft API spec'? This can't be undone." with Delete / Cancel, anchored to the row.</summary>
    private static void ShowDeleteFlyout(FrameworkElement anchor, string prompt, Func<Task> delete)
    {
        var flyout = new Flyout { ShouldConstrainToRootBounds = true };
        var confirm = new Button { Content = Strings.GetString("Checklists_DeleteButton") };
        var cancel = new Button { Content = Strings.GetString("Common_Cancel"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(confirm, "Checklists_DeleteConfirmButton");
        AutomationProperties.SetAutomationId(cancel, "Checklists_DeleteCancelButton");
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            _ = delete();
        };
        cancel.Click += (_, _) => flyout.Hide();

        // Cancel is focused first, so Enter never deletes by accident (design §5.7).
        flyout.Opened += (_, _) => cancel.Focus(FocusState.Programmatic);
        flyout.Content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 320,
            Children =
            {
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { confirm, cancel } },
            },
        };
        flyout.ShowAt(anchor);
    }

    private void OnGeneralAddBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox { Tag: GeneralTodosViewModel general } && general.AddItemCommand.CanExecute(null))
        {
            e.Handled = true;
            general.AddItemCommand.Execute(null);
        }
    }

    private void OnReviewsAddBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox { Tag: ReviewsViewModel reviews } && reviews.AddReviewCommand.CanExecute(null))
        {
            e.Handled = true;
            reviews.AddReviewCommand.Execute(null);
        }
    }

    private void OnGroupAddBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox { Tag: ChecklistGroupViewModel group } && group.AddItemCommand.CanExecute(null))
        {
            e.Handled = true;
            group.AddItemCommand.Execute(null);
        }
    }

    private void OnAddToTaskOpening(object? sender, object e)
    {
        TaskPickerBox.Text = string.Empty;
        TaskPickerBox.ItemsSource = ViewModel.TaskChoices(string.Empty);
    }

    private void OnTaskPickerTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = ViewModel.TaskChoices(sender.Text);
        }
    }

    private void OnTaskPicked(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var matches = ViewModel.TaskChoices(args.QueryText);
        var choice = args.ChosenSuggestion as TaskChoice ?? (matches.Count > 0 ? matches[0] : null);
        if (choice is not null)
        {
            AddToTaskFlyout.Hide();
            ViewModel.AddToTask(choice.Id);
        }
    }

    /// <summary>"Add to task…" and "New to-do": brings the card into view and focuses its Add box (B-09).</summary>
    private void OnFocusAddBoxRequested(object? sender, string groupKey) =>
        BringCardIntoView(groupKey, "Checklists_AddItemBox_" + groupKey);

    private void BringCardIntoView(string groupKey, string? focusAutomationId)
    {
        var attempts = 0;
        void TryFocus()
        {
            var index = IndexOfCard(groupKey);
            if (index >= 0 && GroupsRepeater.GetOrCreateElement(index) is FrameworkElement card)
            {
                card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = Common.Services.MotionSettings.AnimationsEnabled });
                if (focusAutomationId is null)
                {
                    return;
                }

                if (FindByAutomationId(card, focusAutomationId) is Control box)
                {
                    box.Focus(FocusState.Keyboard);
                    return;
                }
            }

            // The group appears after the filter change reaches the list; try again on the next few passes.
            if (++attempts < 10)
            {
                DispatcherQueue.TryEnqueue(TryFocus);
            }
        }

        DispatcherQueue.TryEnqueue(TryFocus);
    }

    private int IndexOfCard(string groupKey)
    {
        if (groupKey == GeneralTodosViewModel.GroupKey)
        {
            return 0;
        }

        if (groupKey == ReviewsCardKey)
        {
            return 1;
        }

        for (var i = 0; i < ViewModel.Cards.Count; i++)
        {
            if (ViewModel.Cards[i] is ChecklistGroupViewModel group && group.Key.Value.ToString("N") == groupKey)
            {
                return i;
            }
        }

        return -1;
    }

    private static DependencyObject? FindByAutomationId(DependencyObject root, string automationId)
    {
        if (AutomationProperties.GetAutomationId(root) == automationId)
        {
            return root;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindByAutomationId(VisualTreeHelper.GetChild(root, i), automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void ApplyBreakpoint(double width)
    {
        // T-08.1 / design §4.5: L 2–3 columns, M 1–2, S one full-width column; padding L 32/24, M 24/16, S 16/12.
        var padding = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);
        GroupsRepeater.Margin = new Thickness(padding, 0, padding, 24);
        NoMatchesBar.Margin = new Thickness(padding, 0, padding, 8);
        GroupsLayout.MaxColumns = width >= 1008 ? 3 : width >= 640 ? 2 : 1;
        GroupsLayout.MinColumnWidth = width >= 640 ? 380 : 0;
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
