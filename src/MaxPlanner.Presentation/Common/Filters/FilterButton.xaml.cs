using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Common.Filters;

/// <summary>
/// The Filters button with its InfoBadge and draft flyout (design §2.6). Code-behind only creates and drops the draft
/// and moves focus (design §7.7: after Apply or Cancel focus returns to the Filters button).
/// </summary>
public sealed partial class FilterButton : UserControl
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(
        nameof(Scope), typeof(FilterScopeViewModel), typeof(FilterButton), new PropertyMetadata(null, (d, _) => ((FilterButton)d).Bindings.Update()));

    public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(
        nameof(Draft), typeof(FilterDraftViewModel), typeof(FilterButton), new PropertyMetadata(null));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(string), typeof(FilterButton), new PropertyMetadata(string.Empty, (d, _) => ((FilterButton)d).ApplyAutomationIds()));

    private bool _isApplying;

    public FilterButton() => InitializeComponent();

    public FilterScopeViewModel? Scope
    {
        get => (FilterScopeViewModel?)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>The draft being edited while the flyout is open; null otherwise.</summary>
    public FilterDraftViewModel? Draft
    {
        get => (FilterDraftViewModel?)GetValue(DraftProperty);
        private set
        {
            SetValue(DraftProperty, value);
            Bindings.Update();
        }
    }

    /// <summary>The page prefix of the AutomationIds, e.g. <c>Board</c> (design §8.1).</summary>
    public string PageKey
    {
        get => (string)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    /// <summary>An AutomationId for a part of a repeated field: <c>{id}_{suffix}</c>.</summary>
    public static string Suffix(string id, string suffix) => id + "_" + suffix;

    public void Open() => FiltersFlyout.ShowAt(FiltersButton);

    private void ApplyAutomationIds()
    {
        AutomationProperties.SetAutomationId(FiltersButton, PageKey + "_FiltersButton");
        AutomationProperties.SetAutomationId(ApplyButton, PageKey + "_FiltersApplyButton");
        AutomationProperties.SetAutomationId(CancelButton, PageKey + "_FiltersCancelButton");
        AutomationProperties.SetAutomationId(ClearButton, PageKey + "_FiltersClearButton");
    }

    private void OnFlyoutOpening(object? sender, object e)
    {
        Draft = Scope?.CreateDraft();
        if (XamlRoot is { } root)
        {
            // Design §2.6: at most 80% of the window (less the presenter padding). The star row gives the fields what
            // the pinned header and footer leave; the 240 floor keeps the footer usable in very short windows.
            FlyoutRoot.MaxHeight = Math.Clamp((root.Size.Height * 0.8) - 32, 240, 640);
        }
    }

    // Light dismiss and Esc close without Apply, which discards the draft (B-10).
    private void OnFlyoutClosed(object? sender, object e)
    {
        Draft = null;
        if (!_isApplying)
        {
            FiltersButton.Focus(FocusState.Programmatic);
        }

        _isApplying = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => FiltersFlyout.Hide();

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (Draft is not { CanApply: true } draft || Scope is null)
        {
            return;
        }

        _isApplying = true;
        Scope.Apply(draft);
        FiltersFlyout.Hide();
        FiltersButton.Focus(FocusState.Programmatic);
    }

    private void OnOpenAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Open();
    }

    private void OnCrBoxGotFocus(object sender, RoutedEventArgs e) => Draft?.CrField?.UpdateSuggestions(string.Empty);

    private void OnCrTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            Draft?.CrField?.UpdateSuggestions(sender.Text);
        }
    }

    private void OnCrSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is FilterCrChoice choice)
        {
            Draft?.CrField?.Choose(choice);
        }
    }

    private void OnCrQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (Draft?.CrField is not { } field)
        {
            return;
        }

        if (args.ChosenSuggestion is FilterCrChoice chosen)
        {
            field.Choose(chosen);
        }
        else if (field.Suggestions.FirstOrDefault(s => s.Filter.IsSet && s.Display.Contains(args.QueryText, StringComparison.CurrentCultureIgnoreCase)) is { } best)
        {
            field.Choose(best);
        }

        // A typed text that matches nothing keeps the chosen CR; the box shows it again.
        sender.Text = field.Text;
    }
}
