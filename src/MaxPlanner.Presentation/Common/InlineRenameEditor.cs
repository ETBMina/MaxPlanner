using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The view side of an inline rename box (design §4.5 checklist items, §4.6 Settings rows): the keys and the first
/// focus. What a commit runs, where focus goes after it, and what leaving the box does stay with each page.
/// </summary>
internal static class InlineRenameEditor
{
    /// <summary>
    /// Enter runs <paramref name="commit"/> and Esc runs <paramref name="cancel"/>. Both keys are marked handled first,
    /// so the row or list under the box does not act on them as well; every other key is left to the box.
    /// </summary>
    public static void HandleKey(KeyRoutedEventArgs e, Action commit, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(cancel);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                commit();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                cancel();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// After the rename began: on the next dispatcher turn, once the box the view model just showed can take focus,
    /// focuses the box that <paramref name="find"/> returns (it runs in that turn) with the keyboard and selects its text.
    /// </summary>
    public static void FocusWhenShown(DispatcherQueue queue, Func<TextBox?> find)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(find);
        queue.TryEnqueue(() =>
        {
            if (find() is { } box)
            {
                box.Focus(FocusState.Keyboard);
                box.SelectAll();
            }
        });
    }
}
