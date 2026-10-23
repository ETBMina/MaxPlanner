using System.ComponentModel;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Common.Fields;

/// <summary>
/// Builds the native editor of one typed custom field (design §5.0 input table): TextBox, multiline TextBox,
/// NumberBox, CalendarDatePicker with Clear, ComboBox, or TokenPicker. Editors are created in code because the
/// field set is configurable; values flow both ways through change events, the same way for dialogs and Grid cells.
/// </summary>
internal static class CustomFieldEditors
{
    /// <param name="automationPrefix">For example <c>TaskEditor</c>; the editor's AutomationId is <c>{prefix}_Custom_{key}</c> (design §8.1).</param>
    /// <param name="showHeader">False for Grid cells, whose column header names the field.</param>
    /// <returns>The editor element and the control that should receive focus.</returns>
    public static (FrameworkElement Element, Control Focus) Create(CustomFieldEditorViewModel field, string automationPrefix, bool showHeader)
    {
        ArgumentNullException.ThrowIfNull(field);
        var id = $"{automationPrefix}_Custom_{field.Key}";
        var header = showHeader ? field.Header : null;
        (FrameworkElement element, Control focus) = field.Kind switch
        {
            FieldKind.Number => Number(field, header),
            FieldKind.Date => Date(field, header, id),
            FieldKind.Lookup => Lookup(field, header),
            FieldKind.People or FieldKind.Tags => Tokens(field, header ?? string.Empty, id),
            _ => Text(field, header),
        };
        if (field.Kind is not (FieldKind.People or FieldKind.Tags))
        {
            AutomationProperties.SetAutomationId(focus, id);
        }

        // Grid cells and dialogs name the editor by the field label; the dialog header shows it too.
        AutomationProperties.SetName(focus, field.Field.Label.Display);
        AutomationProperties.SetIsRequiredForForm(focus, field.Field.IsRequired);
        return (element, focus);
    }

    /// <summary>An editor plus its inline error (design §5.0), for the editors' field lists.</summary>
    public static (FrameworkElement Element, Control Focus) CreateWithError(CustomFieldEditorViewModel field, string automationPrefix)
    {
        var (editor, focus) = Create(field, automationPrefix, showHeader: true);
        var error = new FieldError { Text = field.Error };
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CustomFieldEditorViewModel.Error))
            {
                error.Text = field.Error;
            }
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(editor);
        panel.Children.Add(error);
        return (panel, focus);
    }

    private static (FrameworkElement, Control) Text(CustomFieldEditorViewModel field, string? header)
    {
        var box = new TextBox
        {
            Header = header,
            Text = field.Text,
            MaxLength = field.Kind switch
            {
                FieldKind.MultilineText => DomainLimits.LongText,
                FieldKind.Url => DomainLimits.Url,
                _ => DomainLimits.CustomText,
            },
            IsReadOnly = field.IsReadOnly,
        };
        if (field.IsMultiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 96;
            box.MaxHeight = 320;
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }
        else if (field.Kind == FieldKind.Url)
        {
            box.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Url) } };
        }

        box.TextChanged += (_, _) => field.Text = box.Text;
        Follow(field, e =>
        {
            if (e == nameof(CustomFieldEditorViewModel.Text) && box.Text != field.Text)
            {
                box.Text = field.Text;
            }
            else if (e == nameof(CustomFieldEditorViewModel.IsReadOnly))
            {
                box.IsReadOnly = field.IsReadOnly;
            }
        });
        return (box, box);
    }

    private static (FrameworkElement, Control) Number(CustomFieldEditorViewModel field, string? header)
    {
        var box = new NumberBox
        {
            Header = header,
            Value = field.Number,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            IsEnabled = !field.IsReadOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.ValueChanged += (_, args) => field.Number = args.NewValue;
        Follow(field, e =>
        {
            if (e == nameof(CustomFieldEditorViewModel.Number) && !SameNumber(box.Value, field.Number))
            {
                box.Value = field.Number;
            }
            else if (e == nameof(CustomFieldEditorViewModel.IsReadOnly))
            {
                box.IsEnabled = !field.IsReadOnly;
            }
        });
        return (box, box);
    }

    private static (FrameworkElement, Control) Date(CustomFieldEditorViewModel field, string? header, string id)
    {
        var picker = new CalendarDatePicker
        {
            Header = header,
            Date = field.Date,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !field.IsReadOnly,
        };
        var clear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = !field.IsReadOnly,
        };
        var clearName = ClearName.Value;
        AutomationProperties.SetName(clear, string.Format(System.Globalization.CultureInfo.CurrentCulture, clearName, field.Field.Label.Display));
        AutomationProperties.SetAutomationId(clear, id + "ClearButton");
        ToolTipService.SetToolTip(clear, AutomationProperties.GetName(clear));
        picker.DateChanged += (_, args) => field.Date = args.NewDate;
        clear.Click += (_, _) => field.Date = null;
        Follow(field, e =>
        {
            if (e == nameof(CustomFieldEditorViewModel.Date) && picker.Date != field.Date)
            {
                picker.Date = field.Date;
            }
            else if (e == nameof(CustomFieldEditorViewModel.IsReadOnly))
            {
                picker.IsEnabled = clear.IsEnabled = !field.IsReadOnly;
            }
        });
        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(clear, 1);
        grid.Children.Add(picker);
        grid.Children.Add(clear);
        return (grid, picker);
    }

    private static (FrameworkElement, Control) Lookup(CustomFieldEditorViewModel field, string? header)
    {
        // Items before selection, so the selection never applies to an empty list.
        var combo = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !field.IsReadOnly };
        combo.ItemsSource = field.Options;
        combo.SelectedItem = field.SelectedOption;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is FieldOptionChoice choice)
            {
                field.SelectedOption = choice;
            }
        };
        Follow(field, e =>
        {
            if (e == nameof(CustomFieldEditorViewModel.SelectedOption) && !Equals(combo.SelectedItem, field.SelectedOption))
            {
                combo.SelectedItem = field.SelectedOption;
            }
            else if (e == nameof(CustomFieldEditorViewModel.IsReadOnly))
            {
                combo.IsEnabled = !field.IsReadOnly;
            }
        });
        return (combo, combo);
    }

    private static (FrameworkElement, Control) Tokens(CustomFieldEditorViewModel field, string header, string id)
    {
        var picker = new TokenPicker { ViewModel = field.Tokens, Header = header, BoxAutomationId = id, IsEditable = !field.IsReadOnly };
        Follow(field, e =>
        {
            if (e == nameof(CustomFieldEditorViewModel.IsReadOnly))
            {
                picker.IsEditable = !field.IsReadOnly;
            }
        });
        return (picker, picker);
    }

    private static void Follow(CustomFieldEditorViewModel field, Action<string?> onChanged)
    {
        void Handler(object? sender, PropertyChangedEventArgs e) => onChanged(e.PropertyName);
        field.PropertyChanged += Handler;
    }

    private static bool SameNumber(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || a.Equals(b);

    private static readonly Lazy<string> ClearName = new(() => new Services.ResourceStrings().GetString("CustomField_ClearDateName"));
}

/// <summary>
/// The custom fields group of the task and CR editors (design §5.1 and §5.3, B-11): one native editor per
/// active field with its inline error. Rebuilt when <see cref="ViewModel"/> changes.
/// </summary>
public sealed partial class CustomFieldsPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(CustomFieldsEditorViewModel), typeof(CustomFieldsPanel), new PropertyMetadata(null, (d, _) => ((CustomFieldsPanel)d).Rebuild()));

    public static readonly DependencyProperty AutomationPrefixProperty = DependencyProperty.Register(
        nameof(AutomationPrefix), typeof(string), typeof(CustomFieldsPanel), new PropertyMetadata("Editor", (d, _) => ((CustomFieldsPanel)d).Rebuild()));

    private readonly StackPanel _panel = new() { Spacing = 12 };
    private readonly Dictionary<string, Control> _focusTargets = new(StringComparer.Ordinal);

    public CustomFieldsPanel()
    {
        IsTabStop = false;
        Content = _panel;
    }

    public CustomFieldsEditorViewModel? ViewModel
    {
        get => (CustomFieldsEditorViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public string AutomationPrefix
    {
        get => (string)GetValue(AutomationPrefixProperty);
        set => SetValue(AutomationPrefixProperty, value);
    }

    /// <summary>Moves focus to a field's editor (first invalid field after Save, design §5.0).</summary>
    public bool FocusField(string key) => _focusTargets.TryGetValue(key, out var target) && target.Focus(FocusState.Programmatic);

    private void Rebuild()
    {
        _panel.Children.Clear();
        _focusTargets.Clear();
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        foreach (var field in viewModel.Fields)
        {
            var (element, focus) = CustomFieldEditors.CreateWithError(field, AutomationPrefix);
            _focusTargets[field.Key] = focus;
            _panel.Children.Add(element);
        }
    }
}
