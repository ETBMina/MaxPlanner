using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>
/// The Filters button with its InfoBadge and cascading filter menu (design §2.6). Code-behind only creates and drops
/// the menu, opens the nested submenus, and moves focus (Right/Enter opens a submenu, Left/Esc closes it; when the menu
/// closes, focus returns to the Filters button, design §7.7). As in a MenuFlyout, resting the mouse on a row (or on
/// Customised) for a moment opens its submenu without moving focus, and resting on another row switches it. Every value
/// change is in the view models.
/// </summary>
public sealed partial class FilterButton : UserControl
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(
        nameof(Scope), typeof(FilterScopeViewModel), typeof(FilterButton), new PropertyMetadata(null, (d, _) => ((FilterButton)d).Bindings.Update()));

    public static readonly DependencyProperty MenuProperty = DependencyProperty.Register(
        nameof(Menu), typeof(FilterMenuViewModel), typeof(FilterButton), new PropertyMetadata(null));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(string), typeof(FilterButton), new PropertyMetadata(string.Empty, (d, _) => ((FilterButton)d).ApplyAutomationIds()));

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    private readonly Flyout _submenu;
    private readonly ContentControl _submenuContent;
    private readonly Flyout _rangeFlyout;
    private readonly ContentControl _rangeContent;
    private readonly DispatcherQueueTimer _hoverTimer;
    private Action? _hoverAction;
    private Control? _submenuOwner;
    private Control? _rangeOwner;

    // A submenu opened by click or keyboard takes focus (and gives it back on close); one opened by hover does not.
    private bool _submenuHasFocus;
    private bool _rangeHasFocus;

    public FilterButton()
    {
        InitializeComponent();
        _submenu = (Flyout)Resources["SubmenuFlyout"];
        _submenuContent = (ContentControl)_submenu.Content;
        _submenu.Opened += (_, _) => FocusIf(_submenuHasFocus, _submenuContent);
        _submenu.Closed += OnSubmenuClosed;
        _submenuContent.KeyDown += OnSubmenuKeyDown;
        _rangeFlyout = (Flyout)Resources["RangeFlyout"];
        _rangeContent = (ContentControl)_rangeFlyout.Content;
        _rangeFlyout.Opened += (_, _) => FocusIf(_rangeHasFocus, _rangeContent);
        _rangeFlyout.Closed += (_, _) =>
        {
            if (_rangeHasFocus)
            {
                Restore(ref _rangeOwner);
            }

            _rangeOwner = null;
        };
        _rangeContent.KeyDown += OnRangeKeyDown;

        // MenuFlyout's hover delay: resting on an item opens (or switches to) its submenu.
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(300);
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) =>
        {
            var action = _hoverAction;
            _hoverAction = null;
            action?.Invoke();
        };
    }

    public FilterScopeViewModel? Scope
    {
        get => (FilterScopeViewModel?)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>The menu while it is open; null otherwise.</summary>
    public FilterMenuViewModel? Menu
    {
        get => (FilterMenuViewModel?)GetValue(MenuProperty);
        private set
        {
            SetValue(MenuProperty, value);
            Bindings.Update();
        }
    }

    /// <summary>The page prefix of the AutomationIds, e.g. <c>Board</c> (design §8.1).</summary>
    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    /// <summary>
    /// The priority-rank and completed-status glyph colours. The styles use theme resources, so they follow the
    /// element's own (in-app) theme. In High Contrast none applies: the glyph keeps the item's foreground, which
    /// follows its hover and pressed states. <see cref="FilterGlyphTone.Default"/> always inherits it. The menu is
    /// rebuilt each time it opens, so a High Contrast switch shows on the next open.
    /// </summary>
    public static Style? GlyphStyle(FilterGlyphTone tone) => Accessibility.HighContrast ? null : tone switch
    {
        FilterGlyphTone.Critical => (Style)Microsoft.UI.Xaml.Application.Current.Resources["FilterMenuCriticalGlyphStyle"],
        FilterGlyphTone.Caution => (Style)Microsoft.UI.Xaml.Application.Current.Resources["FilterMenuCautionGlyphStyle"],
        FilterGlyphTone.Success => (Style)Microsoft.UI.Xaml.Application.Current.Resources["FilterMenuSuccessGlyphStyle"],
        _ => null,
    };

    public void Open() => FiltersFlyout.ShowAt(FiltersButton);

    private static void FocusIf(bool takesFocus, DependencyObject root)
    {
        if (takesFocus)
        {
            FocusFirst(root);
        }
    }

    private static void FocusFirst(DependencyObject root)
    {
        if (FocusManager.FindFirstFocusableElement(root) is Control first)
        {
            first.Focus(FocusState.Programmatic);
        }
    }

    private static void Restore(ref Control? owner)
    {
        owner?.Focus(FocusState.Programmatic);
        owner = null;
    }

    private void ApplyAutomationIds() => AutomationProperties.SetAutomationId(FiltersButton, PageKey + "_FiltersButton");

    private void OnFlyoutOpening(object? sender, object e)
    {
        Menu = Scope?.CreateMenu();
        if (Menu is { } menu)
        {
            menu.Detached += OnMenuDetached;
        }

        if (XamlRoot is { } root)
        {
            // Design §2.6: at most 80% of the window, less the header row and the presenter padding.
            RowsScroller.MaxHeight = Math.Clamp((root.Size.Height * 0.8) - 56, 160, 640);
        }
    }

    private void OnFlyoutOpened(object? sender, object e) => FocusFirst(RowsList);

    // Esc and light dismiss only close: every change was already applied, except text still being typed.
    private void OnFlyoutClosed(object? sender, object e)
    {
        CancelHover();
        _rangeFlyout.Hide();
        _submenu.Hide();
        if (Menu is { } menu)
        {
            menu.CommitPendingText();
            menu.Detached -= OnMenuDetached;
            Scope?.ReleaseMenu(menu);
        }

        // Release the last menu graph (rows, editors, calendar days) held by the reused flyouts.
        _submenuContent.Content = null;
        _rangeContent.Content = null;
        _submenuOwner = null;
        _rangeOwner = null;
        Menu = null;
        FiltersButton.Focus(FocusState.Programmatic);
    }

    // The scope changed from outside the menu (Ctrl+Shift+L, a chip, a drill-down): the open menu is stale, so it closes.
    private void OnMenuDetached(object? sender, EventArgs e) => FiltersFlyout.Hide();

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FilterMenuRowViewModel row } owner)
        {
            CancelHover();
            OpenSubmenu(row, owner, takeFocus: true);
        }
    }

    private void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Right && sender is Control { DataContext: FilterMenuRowViewModel row } owner)
        {
            e.Handled = true;
            OpenSubmenu(row, owner, takeFocus: true);
        }
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsMouseLike(e) && sender is Control { DataContext: FilterMenuRowViewModel row } owner)
        {
            ScheduleHover(() =>
            {
                if (!(_submenu.IsOpen && ReferenceEquals(_submenuOwner, owner)))
                {
                    OpenSubmenu(row, owner, takeFocus: false);
                }
            });
        }
    }

    private void OnHoverPointerExited(object sender, PointerRoutedEventArgs e) => CancelHover();

    // Resting on another item of a submenu closes the range picker opened from Customised, as a MenuFlyout does.
    private void OnSubmenuItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsMouseLike(e) && _rangeFlyout.IsOpen)
        {
            ScheduleHover(_rangeFlyout.Hide);
        }
    }

    private static bool IsMouseLike(PointerRoutedEventArgs e) => e.Pointer.PointerDeviceType != PointerDeviceType.Touch;

    private void ScheduleHover(Action action)
    {
        _hoverTimer.Stop();
        _hoverAction = action;
        _hoverTimer.Start();
    }

    private void CancelHover()
    {
        _hoverTimer.Stop();
        _hoverAction = null;
    }

    // Transient: the submenu does not take focus or block the parent menu, so the pointer can rest on another row to
    // switch it. Click and keyboard opens move focus in themselves.
    private void OpenSubmenu(FilterMenuRowViewModel row, Control owner, bool takeFocus)
    {
        _rangeFlyout.Hide();
        _submenuOwner = owner;
        _submenuHasFocus = takeFocus;
        _submenuContent.Content = row.Editor;
        if (_submenu.IsOpen && takeFocus)
        {
            // Already open (by hover): Opened will not fire again, so move focus in once the new content is laid out.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusFirst(_submenuContent));
        }

        _submenu.ShowAt(owner, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop, ShowMode = FlyoutShowMode.Transient });
    }

    private void OnSubmenuClosed(object? sender, object e)
    {
        _rangeFlyout.Hide();
        Menu?.CommitPendingText();

        // Clicking another row light-dismisses this submenu and reopens it for that row; the late Closed must not
        // pull focus back to the previous row. A hover-opened submenu never had focus to give back.
        if (!_submenu.IsOpen)
        {
            if (_submenuHasFocus)
            {
                Restore(ref _submenuOwner);
            }

            _submenuOwner = null;
        }
    }

    // Left closes the submenu (text boxes keep Left for the caret: they handle it before it bubbles here).
    private void OnSubmenuKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left)
        {
            e.Handled = true;
            _submenu.Hide();
        }
    }

    private void OnCustomisedClick(object sender, RoutedEventArgs e)
    {
        CancelHover();
        OpenRange(sender, takeFocus: true);
    }

    private void OnCustomisedPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (IsMouseLike(e))
        {
            ScheduleHover(() =>
            {
                if (!(_rangeFlyout.IsOpen && ReferenceEquals(_rangeOwner, sender)))
                {
                    OpenRange(sender, takeFocus: false);
                }
            });
        }
    }

    // While the end of a range is being picked, the day under the pointer or keyboard focus previews it.
    private void OnDayPointerEntered(object sender, PointerRoutedEventArgs e) => PreviewDay(sender);

    private void OnDayGotFocus(object sender, RoutedEventArgs e) => PreviewDay(sender);

    private void OnDaysPointerExited(object sender, PointerRoutedEventArgs e) =>
        (_rangeContent.Content as DateRangePickerViewModel)?.Preview(null);

    private void PreviewDay(object sender)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayViewModel day } && _rangeContent.Content is DateRangePickerViewModel range)
        {
            range.Preview(day.Date);
        }
    }

    // Esc closes the range picker; Left does too where it cannot move further left: on a day in the first column of
    // the first month, or on the previous-month button or Clear range link at the left edge.
    private void OnRangeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Left || _rangeContent.Content is not DateRangePickerViewModel range)
        {
            return;
        }

        var atLeftEdge = e.OriginalSource switch
        {
            FrameworkElement { DataContext: CalendarDayViewModel day } =>
                day.Date.DayOfWeek == DayOfWeek.Sunday && range.Months[0].Days.Contains(day),
            FrameworkElement element => AutomationProperties.GetAutomationId(element) is "Filters_DateRange_PrevMonth" or "Filters_DateRange_Clear",
            _ => false,
        };
        if (atLeftEdge)
        {
            e.Handled = true;
            _rangeFlyout.Hide();
        }
    }

    private void OnCustomisedKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Right)
        {
            e.Handled = true;
            OpenRange(sender, takeFocus: true);
        }
    }

    private void OpenRange(object sender, bool takeFocus)
    {
        if (sender is not Control owner)
        {
            return;
        }

        var range = owner.DataContext switch
        {
            FilterDateFieldViewModel date => date.Range,
            ActiveDateFieldViewModel active => active.Range,
            _ => null,
        };
        if (range is null)
        {
            return;
        }

        _rangeOwner = owner;
        _rangeHasFocus = takeFocus;
        _rangeContent.Content = range;
        if (_rangeFlyout.IsOpen && takeFocus)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusFirst(_rangeContent));
        }

        _rangeFlyout.ShowAt(owner, new FlyoutShowOptions { Placement = FlyoutPlacementMode.RightEdgeAlignedTop, ShowMode = FlyoutShowMode.Transient });
    }

    private void OnContainsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is FrameworkElement { DataContext: FilterTextFieldViewModel field })
        {
            e.Handled = true;
            field.Commit();
        }
    }

    private void OnContainsLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterTextFieldViewModel field })
        {
            field.Commit();
        }
    }

    private void OnOpenAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Open();
    }

    private void OnCrBoxGotFocus(object sender, RoutedEventArgs e) => CrField(sender)?.UpdateSuggestions(string.Empty);

    private void OnCrTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            CrField(sender)?.UpdateSuggestions(sender.Text);
        }
    }

    private void OnCrSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is FilterCrChoice choice)
        {
            CrField(sender)?.Choose(choice);
        }
    }

    private void OnCrQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (CrField(sender) is not { } field)
        {
            return;
        }

        field.Submit(args.QueryText, args.ChosenSuggestion as FilterCrChoice);

        // Empty text, or text that matches nothing, keeps the chosen CR; the box shows it again.
        sender.Text = field.Text;
    }

    /// <summary>The CR field behind a box event; none once the menu has closed (a late event from a closing popup).</summary>
    private FilterCrFieldViewModel? CrField(object sender) =>
        Menu is not null ? (sender as FrameworkElement)?.DataContext as FilterCrFieldViewModel : null;
}

/// <summary>Picks the submenu template for a filter menu row's editor.</summary>
public sealed partial class FilterEditorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ChoiceTemplate { get; set; }

    public DataTemplate? DateTemplate { get; set; }

    public DataTemplate? ActiveDateTemplate { get; set; }

    public DataTemplate? CrTemplate { get; set; }

    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? NumberTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        FilterChoiceGroupViewModel => ChoiceTemplate,
        FilterDateFieldViewModel => DateTemplate,
        ActiveDateFieldViewModel => ActiveDateTemplate,
        FilterCrFieldViewModel => CrTemplate,
        FilterTextFieldViewModel => TextTemplate,
        FilterNumberFieldViewModel => NumberTemplate,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
