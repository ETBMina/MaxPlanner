using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MaxPlanner.Presentation.Checklists;

/// <summary>The Checklists page view. Code-behind forwards checkbox clicks and Enter to the group ViewModels.</summary>
public sealed partial class ChecklistsPageView : UserControl
{
    public ChecklistsPageView(ChecklistsPageViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        SizeChanged += (_, e) => ApplyBreakpoint(e.NewSize.Width);
    }

    public ChecklistsPageViewModel ViewModel { get; }

    public void FocusTitle() => PageTitle.Focus(FocusState.Programmatic);

    // Checked/Unchecked rather than Click, because UI Automation's Toggle (Narrator, voice access) changes
    // the state without a Click. The checkbox shows the change at once; the committed state follows the
    // save, and a failed save puts the checkbox back (ChecklistItemViewModel.RestoreCommitted). Changes that
    // only mirror the committed value (binding updates, container reuse) are not commands.
    private void OnItemToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ChecklistItemViewModel item } box && box.IsChecked != item.IsChecked && FindGroup(box) is { } group)
        {
            _ = group.ToggleAsync(item, box.IsChecked == true);
        }
    }

    private void OnAddBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox box && FindGroup(box) is { } group && group.AddItemCommand.CanExecute(null))
        {
            e.Handled = true;
            group.AddItemCommand.Execute(null);
        }
    }

    private void ApplyBreakpoint(double width)
    {
        // T-08.1: L 32/24, M 24/16, S 16/12 padding; groups fill the width up to 880 epx.
        var padding = width >= 1008 ? 32 : width >= 640 ? 24 : 16;
        HeaderRoot.Padding = width >= 1008 ? new Thickness(32, 24, 32, 12) : width >= 640 ? new Thickness(24, 16, 24, 12) : new Thickness(16, 12, 16, 8);
        GroupsRepeater.Margin = new Thickness(padding, 0, padding, 24);
        GroupsRepeater.Width = Math.Max(0, Math.Min(880, width - (2 * padding)));
    }

    private static ChecklistGroupViewModel? FindGroup(DependencyObject element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: ChecklistGroupViewModel group })
            {
                return group;
            }
        }

        return null;
    }
}
