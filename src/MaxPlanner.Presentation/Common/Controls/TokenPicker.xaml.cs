using MaxPlanner.Presentation.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// People and tags picker (design §5.0, ADR 0021). The ViewModel holds the chips and suggestions; this view
/// only forwards the box's submit and the chips' remove actions.
/// </summary>
public sealed partial class TokenPicker : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(TokenPickerViewModel), typeof(TokenPicker), new PropertyMetadata(null, (d, _) => ((TokenPicker)d).Bindings.Update()));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(TokenPicker), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BoxAutomationIdProperty = DependencyProperty.Register(
        nameof(BoxAutomationId), typeof(string), typeof(TokenPicker), new PropertyMetadata(string.Empty, (d, e) =>
            AutomationProperties.SetAutomationId(((TokenPicker)d).Box, (string)e.NewValue)));

    public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
        nameof(IsEditable), typeof(bool), typeof(TokenPicker), new PropertyMetadata(true));

    private readonly PointerEventHandler _outsidePressHandler;
    private UIElement? _pressRoot;
    private bool _hasFocusWithin;
    private bool _ignoreNextEmptyText;

    public TokenPicker()
    {
        InitializeComponent();
        _outsidePressHandler = OnPressWhileListOpen;

        // Handled events too: the inner text box marks the press handled. A click in an already focused box reopens
        // the list after Esc closed it.
        Box.AddHandler(PointerPressedEvent, new PointerEventHandler(OnBoxPointerPressed), true);
        Box.RegisterPropertyChangedCallback(AutoSuggestBox.IsSuggestionListOpenProperty, OnSuggestionListOpenChanged);
        Unloaded += (_, _) => DetachPressRoot();
    }

    public TokenPickerViewModel? ViewModel
    {
        get => (TokenPickerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>The AutomationId of the search box, for example <c>TaskEditor_DesignersPicker</c> (design §8.1).</summary>
    public string BoxAutomationId
    {
        get => (string)GetValue(BoxAutomationIdProperty);
        set => SetValue(BoxAutomationIdProperty, value);
    }

    public bool IsEditable
    {
        get => (bool)GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public void FocusBox() => Box.Focus(FocusState.Programmatic);

    // Design §5.0: the list opens when focus enters the picker, before typing. Only an entry counts: opening the list
    // makes the AutoSuggestBox focus itself, and Esc closes it and then focuses its text box with FocusState.Keyboard.
    // Treating those internal moves as an entry reopened the list, so the first Esc seemed to do nothing.
    // Programmatic entry (after a chip is removed, or a field focused for a validation error) does not open it.
    private void OnBoxGotFocus(object sender, RoutedEventArgs e)
    {
        var entered = !_hasFocusWithin;
        _hasFocusWithin = true;
        if (entered && e.OriginalSource is Control { FocusState: FocusState.Keyboard or FocusState.Pointer })
        {
            OpenSuggestions(refreshOpenList: false);
        }
    }

    // Focus moving between the box's own parts raises LostFocus too, so whether it left is checked once it settles.
    private void OnBoxLostFocus(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!BoxHasFocus())
            {
                _hasFocusWithin = false;
                _ignoreNextEmptyText = false;
                Box.IsSuggestionListOpen = false;
            }
        });

    // A press in a box that is already focused (GotFocus handles the first one) reopens the list after Esc. An open
    // list is left alone: refilling it under the pointer would replace the item being clicked.
    private void OnBoxPointerPressed(object sender, PointerRoutedEventArgs e) => OpenSuggestions(refreshOpenList: false);

    // Deleting the typed text back to empty lists every available name again. The AutoSuggestBox reports text set
    // through the Query binding as UserInput too, so the reset after a pick is skipped explicitly. The binding may
    // update Query after this event, so the list is filled once it has.
    private void OnBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || sender.Text.Length > 0)
        {
            return;
        }

        if (_ignoreNextEmptyText)
        {
            _ignoreNextEmptyText = false;
            return;
        }

        DispatcherQueue.TryEnqueue(() => OpenSuggestions(refreshOpenList: true));
    }

    private void OpenSuggestions(bool refreshOpenList)
    {
        if (ViewModel is not { } viewModel || !IsEditable || !BoxHasFocus() || (Box.IsSuggestionListOpen && !refreshOpenList))
        {
            return;
        }

        viewModel.ShowSuggestions();
        Box.IsSuggestionListOpen = viewModel.Suggestions.Count > 0;
    }

    // A click on a part of the dialog or flyout that takes no focus leaves the box focused, so the AutoSuggestBox
    // keeps its list open. While it is open, a press anywhere else under this window or popup root closes it.
    private void OnSuggestionListOpenChanged(DependencyObject sender, DependencyProperty property)
    {
        DetachPressRoot();
        if (Box.IsSuggestionListOpen)
        {
            _pressRoot = RootOf(this) as UIElement;
            _pressRoot?.AddHandler(PointerPressedEvent, _outsidePressHandler, true);
        }
    }

    private void DetachPressRoot()
    {
        _pressRoot?.RemoveHandler(PointerPressedEvent, _outsidePressHandler);
        _pressRoot = null;
    }

    // The suggestion list lives in its own popup, whose elements have a different root; a press there is a pick, not
    // an outside click, so only presses rooted where the picker is close the list.
    private void OnPressWhileListOpen(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && !IsWithin(source, Box) && ReferenceEquals(RootOf(source), _pressRoot))
        {
            Box.IsSuggestionListOpen = false;
        }
    }

    private static DependencyObject RootOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            element = parent;
        }

        return element;
    }

    private bool BoxHasFocus()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        return IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, Box);
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var hadText = viewModel.Query.Length > 0;
        _ = args.ChosenSuggestion is TokenSuggestion chosen ? viewModel.Choose(chosen) : viewModel.SubmitQuery();

        // A pick empties a typed query through the binding; that text change must not reopen the full list.
        _ignoreNextEmptyText = hadText && viewModel.Query.Length == 0;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TokenViewModel token })
        {
            ViewModel?.Remove(token);
            Box.Focus(FocusState.Programmatic);
        }
    }

    // Design §8.1: repeated items carry a record key, {picker}_Chip_{Guid:N}; recycled elements get the new item's ID.
    private void OnChipPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement { DataContext: TokenViewModel token } chip && !string.IsNullOrEmpty(BoxAutomationId))
        {
            AutomationProperties.SetAutomationId(chip, BoxAutomationId + "_Chip_" + token.Id.ToString("N"));
        }
    }

    // Design §5.0: Delete removes the focused chip.
    private void OnChipKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            e.Handled = true;
            OnRemoveClick(sender, e);
        }
    }
}
