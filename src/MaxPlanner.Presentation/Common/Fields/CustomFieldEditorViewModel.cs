using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Tasks;

namespace MaxPlanner.Presentation.Common.Fields;

/// <summary>A choice of a Lookup custom field as its ComboBox shows it.</summary>
public sealed record FieldOptionChoice(FieldOptionId? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The draft of one typed custom-field value (B-11, design §5.0): text, multiline text, number, date, lookup,
/// people, tags, or URL. Shared by the task and CR editors and by Grid cells, so every route converts values
/// the same way. Use on the UI thread.
/// </summary>
public sealed partial class CustomFieldEditorViewModel : ObservableObject
{
    private readonly ILocalizedStrings _strings;
    private bool _isLoading;

    public CustomFieldEditorViewModel(FieldDefinition field, WorkflowSnapshot workflow, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        Field = field ?? throw new ArgumentNullException(nameof(field));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        Options = field.Kind == FieldKind.Lookup
            ? [new FieldOptionChoice(null, strings.GetString("Common_None")), .. field.Options.OrderBy(o => o.Order).Select(o => new FieldOptionChoice(o.Id, o.Label.Display))]
            : [];
        if (field.Kind is FieldKind.People or FieldKind.Tags)
        {
            IEnumerable<(Guid, string)> Existing() => field.Kind == FieldKind.People
                ? workflow.People.Select(p => (p.Id.Value, p.Name.Display))
                : workflow.Tags.Select(t => (t.Id.Value, t.Name.Display));
            Tokens = new TokenPickerViewModel(strings, Existing, new NewNameRegistry()) { AllowNew = false };
            Tokens.Changed += (_, _) => RaiseChanged();
        }
    }

    /// <summary>Raised when the user changed the value (dirty tracking).</summary>
    public event EventHandler? Changed;

    public FieldDefinition Field { get; }

    public FieldKind Kind => Field.Kind;

    /// <summary>The field label, with " *" for a required field (design §5.0).</summary>
    public string Header => Field.IsRequired ? Field.Label.Display + " *" : Field.Label.Display;

    /// <summary>The stable key used in AutomationIds, for example <c>TaskEditor_Custom_risk</c> (design §8.1).</summary>
    public string Key => Field.Key.Normalized;

    public IReadOnlyList<FieldOptionChoice> Options { get; }

    /// <summary>The People/Tags picker; null for other kinds.</summary>
    public TokenPickerViewModel? Tokens { get; }

    public bool IsMultiline => Kind == FieldKind.MultilineText;

    public bool IsText => Kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Url;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>NaN when empty, as the NumberBox reports it.</summary>
    [ObservableProperty]
    public partial double Number { get; set; } = double.NaN;

    [ObservableProperty]
    public partial DateTimeOffset? Date { get; set; }

    [ObservableProperty]
    public partial FieldOptionChoice? SelectedOption { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    /// <summary>A comparable form of the draft value, for dirty tracking and conflict review.</summary>
    public string Signature => Kind switch
    {
        FieldKind.Number => double.IsNaN(Number) ? string.Empty : Number.ToString("R", CultureInfo.InvariantCulture),
        FieldKind.Date => DraftDates.FromPicker(Date)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
        FieldKind.Lookup => SelectedOption?.Id?.Value.ToString("N") ?? string.Empty,
        FieldKind.People or FieldKind.Tags => string.Join(',', Tokens!.Ids.Select(id => id.ToString("N"))),
        _ => Text.Trim(),
    };

    /// <summary>The <see cref="Signature"/> a stored value produces once loaded, for comparing drafts with saved records.</summary>
    public static string SignatureOf(FieldDefinition field, CustomValue? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return value switch
        {
            null => string.Empty,
            NumberCustomValue number => ((double)number.Value).ToString("R", CultureInfo.InvariantCulture),
            DateCustomValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LookupCustomValue lookup => field.Options.Any(o => o.Id == lookup.OptionId) ? lookup.OptionId.Value.ToString("N") : string.Empty,
            PeopleCustomValue people => string.Join(',', people.PersonIds.Select(id => id.Value.ToString("N"))),
            TagsCustomValue tags => string.Join(',', tags.TagIds.Select(id => id.Value.ToString("N"))),
            TextCustomValue text => text.Value.Trim(),
            MultilineTextCustomValue text => text.Value.Trim(),
            UrlCustomValue url => url.Url.ToString().Trim(),
            _ => string.Empty,
        };
    }

    /// <summary>The draft as display text (conflict review and the Grid cell's draft value).</summary>
    public string DisplayText => Kind switch
    {
        FieldKind.Number => double.IsNaN(Number) ? string.Empty : Number.ToString("G", CultureInfo.CurrentCulture),
        FieldKind.Date => DraftDates.FromPicker(Date)?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty,
        FieldKind.Lookup => SelectedOption?.Id is null ? string.Empty : SelectedOption.Label,
        FieldKind.People or FieldKind.Tags => string.Join(", ", Tokens!.Tokens.Select(t => t.Name)),
        _ => Text,
    };

    /// <summary>Shows a stored value (or nothing) without raising <see cref="Changed"/>.</summary>
    public void Load(CustomValue? value, WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _isLoading = true;
        Error = null;
        Text = value switch
        {
            TextCustomValue text => text.Value,
            MultilineTextCustomValue text => text.Value,
            UrlCustomValue url => url.Url.ToString(),
            _ => string.Empty,
        };
        Number = value is NumberCustomValue number ? (double)number.Value : double.NaN;
        Date = value is DateCustomValue date ? DraftDates.ToPicker(date.Value) : null;
        SelectedOption = value is LookupCustomValue lookup ? Options.FirstOrDefault(o => o.Id == lookup.OptionId) : (Options.Count > 0 ? Options[0] : null);
        Tokens?.Load(value switch
        {
            PeopleCustomValue people => people.PersonIds.Select(id => (id.Value, workflow.FindPerson(id)?.Name.Display ?? _strings.GetString("Common_Unknown"), false)),
            TagsCustomValue tags => tags.TagIds.Select(id => (id.Value, workflow.FindTag(id)?.Name.Display ?? _strings.GetString("Common_Unknown"), false)),
            _ => [],
        });
        _isLoading = false;
        OnPropertyChanged(nameof(Signature));
        OnPropertyChanged(nameof(DisplayText));
    }

    /// <summary>
    /// The typed value of the draft, or null when it is empty. A value that cannot be converted (an invalid URL,
    /// a number out of range) sets <see cref="Error"/> and returns false; the required rule and text limits are
    /// checked by the use case, which reports them against this field (B-02.9).
    /// </summary>
    public bool TryGetValue(out CustomValue? value)
    {
        value = null;
        Error = null;
        switch (Kind)
        {
            case FieldKind.Text:
                value = string.IsNullOrWhiteSpace(Text) ? null : new TextCustomValue(Text.Trim());
                return true;
            case FieldKind.MultilineText:
                value = string.IsNullOrWhiteSpace(Text) ? null : new MultilineTextCustomValue(Text);
                return true;
            case FieldKind.Url:
                var url = HttpUrl.CreateOptional(Text, "Url");
                if (!url.IsSuccess)
                {
                    Error = _strings.GetString("Error_InvalidUrl");
                    return false;
                }

                value = url.Value is { } u ? new UrlCustomValue(u) : null;
                return true;
            case FieldKind.Number:
                if (double.IsNaN(Number))
                {
                    return true;
                }

                if (double.IsInfinity(Number) || Math.Abs(Number) > (double)decimal.MaxValue)
                {
                    Error = _strings.Format("Error_Invalid", Field.Label.Display);
                    return false;
                }

                value = new NumberCustomValue((decimal)Number);
                return true;
            case FieldKind.Date:
                value = DraftDates.FromPicker(Date) is { } day ? new DateCustomValue(day) : null;
                return true;
            case FieldKind.Lookup:
                value = SelectedOption?.Id is { } option ? new LookupCustomValue(option) : null;
                return true;
            case FieldKind.People:
                value = Tokens!.Ids.Count > 0 ? new PeopleCustomValue([.. Tokens.Ids.Select(id => new PersonId(id))]) : null;
                return true;
            case FieldKind.Tags:
                value = Tokens!.Ids.Count > 0 ? new TagsCustomValue([.. Tokens.Ids.Select(id => new TagId(id))]) : null;
                return true;
            default:
                return true;
        }
    }

    partial void OnTextChanged(string value) => RaiseChanged();

    partial void OnNumberChanged(double value) => RaiseChanged();

    partial void OnDateChanged(DateTimeOffset? value) => RaiseChanged();

    partial void OnSelectedOptionChanged(FieldOptionChoice? value) => RaiseChanged();

    partial void OnIsReadOnlyChanged(bool value)
    {
        if (Tokens is not null)
        {
            Tokens.IsReadOnly = value;
        }
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(Signature));
        OnPropertyChanged(nameof(DisplayText));
        if (!_isLoading)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
