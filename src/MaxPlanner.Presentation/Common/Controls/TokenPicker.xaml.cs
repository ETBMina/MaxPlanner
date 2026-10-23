using MaxPlanner.Presentation.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public TokenPicker() => InitializeComponent();

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

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        _ = args.ChosenSuggestion is TokenSuggestion chosen ? viewModel.Choose(chosen) : viewModel.SubmitQuery();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TokenViewModel token })
        {
            ViewModel?.Remove(token);
            Box.Focus(FocusState.Programmatic);
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
