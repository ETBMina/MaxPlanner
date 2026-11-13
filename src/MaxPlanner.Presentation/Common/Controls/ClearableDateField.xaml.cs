using System.Windows.Input;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A date picker with a Clear button and an optional helper line (design §5.0; B-04 explicit clear). Set
/// <see cref="Header"/> through the field's <c>x:Uid</c>, bind <see cref="Date"/> two-way, and disable the whole field
/// with <c>IsEnabled</c>.
/// </summary>
public sealed partial class ClearableDateField : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(ClearableDateField), new PropertyMetadata(null, (d, e) => ((ClearableDateField)d).Picker.Header = e.NewValue));

    public static readonly DependencyProperty DateProperty = DependencyProperty.Register(
        nameof(Date), typeof(DateTimeOffset?), typeof(ClearableDateField), new PropertyMetadata(null, (d, _) => ((ClearableDateField)d).ShowDate()));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(ClearableDateField), new PropertyMetadata(null, (d, e) => ((ClearableDateField)d).ClearButton.Command = (ICommand?)e.NewValue));

    public static readonly DependencyProperty HelperTextProperty = DependencyProperty.Register(
        nameof(HelperText), typeof(string), typeof(ClearableDateField), new PropertyMetadata(null, (d, _) => ((ClearableDateField)d).ShowHelper()));

    public static readonly DependencyProperty PickerAutomationIdProperty = DependencyProperty.Register(
        nameof(PickerAutomationId), typeof(string), typeof(ClearableDateField), new PropertyMetadata(null, (d, e) => AutomationProperties.SetAutomationId(((ClearableDateField)d).Picker, (string?)e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty ClearButtonAutomationIdProperty = DependencyProperty.Register(
        nameof(ClearButtonAutomationId), typeof(string), typeof(ClearableDateField), new PropertyMetadata(null, (d, e) => AutomationProperties.SetAutomationId(((ClearableDateField)d).ClearButton, (string?)e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty ClearButtonUidProperty = DependencyProperty.Register(
        nameof(ClearButtonUid), typeof(string), typeof(ClearableDateField), new PropertyMetadata(null, (d, _) => ((ClearableDateField)d).ShowClearButtonText()));

    public ClearableDateField() => InitializeComponent();

    /// <summary>The picker's header, usually from the field's <c>x:Uid</c> (<c>{uid}.Header</c>).</summary>
    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>The picked local calendar day; bind it two-way.</summary>
    public DateTimeOffset? Date
    {
        get => (DateTimeOffset?)GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    /// <summary>The Clear button's command; it clears the date and records the explicit clear.</summary>
    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    /// <summary>The caption below the field; null leaves no helper line, while an empty text keeps an empty one.</summary>
    public string? HelperText
    {
        get => (string?)GetValue(HelperTextProperty);
        set => SetValue(HelperTextProperty, value);
    }

    public string? PickerAutomationId
    {
        get => (string?)GetValue(PickerAutomationIdProperty);
        set => SetValue(PickerAutomationIdProperty, value);
    }

    public string? ClearButtonAutomationId
    {
        get => (string?)GetValue(ClearButtonAutomationIdProperty);
        set => SetValue(ClearButtonAutomationIdProperty, value);
    }

    /// <summary>
    /// The <c>x:Uid</c> whose accessible name and tooltip the Clear button shows, for example
    /// <c>TaskEditor_ClearStartDateButton</c>.
    /// </summary>
    public string? ClearButtonUid
    {
        get => (string?)GetValue(ClearButtonUidProperty);
        set => SetValue(ClearButtonUidProperty, value);
    }

    /// <summary>The picker, which takes focus for the field (design §5.0: focus moves to the first invalid field).</summary>
    public CalendarDatePicker DatePicker => Picker;

    private void ShowDate()
    {
        if (!Nullable.Equals(Picker.Date, Date))
        {
            Picker.Date = Date;
        }
    }

    private void OnPickerDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!Nullable.Equals(Picker.Date, Date))
        {
            Date = Picker.Date;
        }
    }

    private void ShowHelper()
    {
        HelperTextBlock.Text = HelperText ?? string.Empty;
        HelperTextBlock.Visibility = HelperText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowClearButtonText()
    {
        if (ClearButtonUid is not { Length: > 0 } uid)
        {
            AutomationProperties.SetName(ClearButton, string.Empty);
            ToolTipService.SetToolTip(ClearButton, null);
            return;
        }

        AutomationProperties.SetName(ClearButton, ResourceStrings.Shared.GetString(XamlResourceKeys.AutomationName(uid)));
        ToolTipService.SetToolTip(ClearButton, ResourceStrings.Shared.GetString(XamlResourceKeys.ToolTip(uid)));
    }
}
