using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The page search box across breakpoints (design §2.3 row 2): 280 epx at L, 200 at M, and at S a Search toggle
/// button that expands a full-width search row under the commands. The same <see cref="AutoSuggestBox"/> instance
/// moves between its header slot and the compact row, so its bindings, text and query state are kept. View-side
/// only; use on the UI thread.
/// </summary>
public sealed class CompactSearch
{
    private const double SmallMaxWidth = 640;
    private const double LargeMinWidth = 1008;

    private readonly AutoSuggestBox _box;
    private readonly Panel _wideHost;
    private readonly int _wideIndex;
    private readonly Panel _compactRow;
    private readonly ToggleButton _toggle;
    private readonly double _wideMaxWidth;
    private readonly bool _hasFixedWidth;
    private bool _isCompact;

    /// <param name="box">The search box, in its header panel when this is created.</param>
    /// <param name="toggle">The S-breakpoint Search button; collapsed at M and L.</param>
    /// <param name="compactRow">An empty full-width panel under the header commands; collapsed until expanded.</param>
    public CompactSearch(AutoSuggestBox box, ToggleButton toggle, Panel compactRow)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _toggle = toggle ?? throw new ArgumentNullException(nameof(toggle));
        _compactRow = compactRow ?? throw new ArgumentNullException(nameof(compactRow));
        _wideHost = box.Parent as Panel ?? throw new ArgumentException("The search box must be the child of a panel.", nameof(box));
        _wideIndex = _wideHost.Children.IndexOf(box);
        _wideMaxWidth = box.MaxWidth;
        _hasFixedWidth = !double.IsNaN(box.Width);
        _toggle.Visibility = Visibility.Collapsed;
        _compactRow.Visibility = Visibility.Collapsed;
        _toggle.Checked += (_, _) => Expand(focus: true);
        _toggle.Unchecked += (_, _) => _compactRow.Visibility = Visibility.Collapsed;
        _box.KeyDown += OnBoxKeyDown;
    }

    /// <summary>Applies the layout for the page width in effective pixels.</summary>
    public void Apply(double pageWidth)
    {
        var compact = pageWidth < SmallMaxWidth;
        if (compact != _isCompact)
        {
            var hadFocus = _box.FocusState != FocusState.Unfocused;
            _isCompact = compact;
            if (compact)
            {
                _wideHost.Children.Remove(_box);
                _compactRow.Children.Add(_box);
                _toggle.Visibility = Visibility.Visible;

                // An active search stays visible: the row opens with its text instead of hiding it.
                _toggle.IsChecked = hadFocus || !string.IsNullOrEmpty(_box.Text);
            }
            else
            {
                _compactRow.Children.Remove(_box);
                _wideHost.Children.Insert(Math.Min(_wideIndex, _wideHost.Children.Count), _box);
                _toggle.Visibility = Visibility.Collapsed;
                _compactRow.Visibility = Visibility.Collapsed;
            }

            // Design §7.7: a breakpoint change keeps focus on the same element when it is still visible.
            if (hadFocus)
            {
                _box.DispatcherQueue.TryEnqueue(() => _box.Focus(FocusState.Programmatic));
            }
        }

        if (compact)
        {
            _box.Width = double.NaN;
            _box.MaxWidth = double.PositiveInfinity;
            _box.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            if (_hasFixedWidth)
            {
                _box.Width = pageWidth >= LargeMinWidth ? 280 : 200;
            }

            _box.MaxWidth = _wideMaxWidth;
            _box.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    /// <summary>Ctrl+F (design §7.1): focuses the search box, expanding the compact row first at S.</summary>
    public void FocusSearch()
    {
        if (_isCompact)
        {
            _toggle.IsChecked = true;
            Expand(focus: true);
            return;
        }

        _box.Focus(FocusState.Keyboard);
    }

    private void Expand(bool focus)
    {
        _compactRow.Visibility = Visibility.Visible;
        if (focus)
        {
            // The row becomes visible in this layout pass; focus it once it can take focus.
            _box.DispatcherQueue.TryEnqueue(() => _box.Focus(FocusState.Keyboard));
        }
    }

    // Esc in an empty compact search closes the row and returns focus to its Search button.
    private void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isCompact && e.Key == VirtualKey.Escape && string.IsNullOrEmpty(_box.Text))
        {
            e.Handled = true;
            _toggle.IsChecked = false;
            _toggle.Focus(FocusState.Keyboard);
        }
    }
}
