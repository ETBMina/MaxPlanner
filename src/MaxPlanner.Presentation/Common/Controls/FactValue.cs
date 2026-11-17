using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The value of a fact in an editor's facts or info area (the task number, owner, CR or service of a record): the value
/// in <see cref="ValueStyle"/>, or, when there is none, a muted "None" (<c>FactNoneTextStyle</c>) that UI Automation
/// reads with its label, for example "Task number: none". One TextBlock, no template.
/// </summary>
public sealed partial class FactValue : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(FactValue), new PropertyMetadata(string.Empty, (d, _) => ((FactValue)d).Show()));

    public static readonly DependencyProperty ValueStyleProperty = DependencyProperty.Register(
        nameof(ValueStyle), typeof(Style), typeof(FactValue), new PropertyMetadata(null, (d, _) => ((FactValue)d).Show()));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private string? _labelUid;
    private bool _isValueSelectable;

    public FactValue()
    {
        IsTabStop = false;
        Content = _text;
        Show();
    }

    /// <summary>The value; null or empty shows "None".</summary>
    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The style of a value; "None" always uses <c>FactNoneTextStyle</c>.</summary>
    public Style? ValueStyle
    {
        get => (Style?)GetValue(ValueStyleProperty);
        set => SetValue(ValueStyleProperty, value);
    }

    /// <summary>The <c>x:Uid</c> of the fact's label; its <c>.Text</c> string names an empty value for UI Automation.</summary>
    public string? LabelUid
    {
        get => _labelUid;
        set
        {
            _labelUid = value;
            Show();
        }
    }

    /// <summary>The AutomationId of the value's text, as the plain TextBlock had it.</summary>
    public string ValueAutomationId
    {
        get => AutomationProperties.GetAutomationId(_text);
        set => AutomationProperties.SetAutomationId(_text, value);
    }

    /// <summary>Whether a value can be selected and copied; "None" never can.</summary>
    public bool IsValueSelectable
    {
        get => _isValueSelectable;
        set
        {
            _isValueSelectable = value;
            Show();
        }
    }

    private void Show()
    {
        var value = Value;
        if (FactValueRules.IsNone(value))
        {
            var strings = ResourceStrings.Shared;
            var none = strings.GetString("Common_None");
            var label = string.IsNullOrEmpty(_labelUid) ? null : strings.GetString(XamlResourceKeys.Text(_labelUid));
            _text.Text = none;
            _text.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["FactNoneTextStyle"];
            _text.IsTextSelectionEnabled = false;
            AutomationProperties.SetName(_text, FactValueRules.NoneName(label, strings.GetString("Common_NoneValueName"), none));
            return;
        }

        _text.Text = value;
        if (ValueStyle is { } style)
        {
            _text.Style = style;
        }
        else
        {
            _text.ClearValue(FrameworkElement.StyleProperty);
        }

        _text.IsTextSelectionEnabled = _isValueSelectable;
        _text.ClearValue(AutomationProperties.NameProperty);
    }
}

/// <summary>The decisions of <see cref="FactValue"/>, kept free of WinUI so tests can check them.</summary>
public static class FactValueRules
{
    /// <summary>A fact without a value shows "None". x:Bind turns a null string into an empty one, so both mean none.</summary>
    public static bool IsNone(string? value) => string.IsNullOrEmpty(value);

    /// <summary>The accessible name of an empty fact: "Task number: none", or just <paramref name="none"/> without a label.</summary>
    /// <param name="label">The fact's label, for example "Task number".</param>
    /// <param name="format">The <c>Common_NoneValueName</c> format, "{0}: none".</param>
    /// <param name="none">The shown text, "None".</param>
    public static string NoneName(string? label, string format, string none) =>
        string.IsNullOrEmpty(label) ? none : string.Format(System.Globalization.CultureInfo.CurrentCulture, format, label);
}
