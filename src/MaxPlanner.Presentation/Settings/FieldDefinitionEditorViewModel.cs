using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Settings;
using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings;

/// <summary>One Lookup option in the field dialog; an existing option keeps its ID so stored values survive a rename.</summary>
public sealed partial class FieldOptionRowViewModel : ObservableObject
{
    public FieldOptionRowViewModel(FieldOptionId id, string label, string removeName)
    {
        Id = id;
        Label = label;
        RemoveName = removeName;
        AutomationId = "FieldDialog_OptionBox_" + id.Value.ToString("N");
        RemoveAutomationId = "FieldDialog_OptionRemoveButton_" + id.Value.ToString("N");
    }

    public FieldOptionId Id { get; }

    public string AutomationId { get; }

    public string RemoveAutomationId { get; }

    public string RemoveName { get; }

    [ObservableProperty]
    public partial string Label { get; set; }
}

/// <summary>
/// The custom field dialog (design §4.6 "Add field…"/"Edit…", B-11): Label*, Key* (proposed from the label and
/// fixed after the first save), Kind* (fixed after creation: no silent retyping), Required, Default, and the
/// options of a Lookup field. Saving runs one Settings use case; errors stay in the dialog.
/// </summary>
public sealed partial class FieldDefinitionEditorViewModel : ObservableObject, IDialogViewModel
{
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkspaceContext _context;
    private readonly SettingsUseCases _useCases;
    private readonly FieldDefinition? _existing;
    private readonly string _stamp = string.Empty;
    private readonly FieldId _id;
    private bool _keyEdited;
    private bool _isProposingKey;
    private OperationId? _retryOperation;

    private FieldDefinitionEditorViewModel(WorkspaceContext context, SettingsUseCases useCases, WorkflowSnapshot workflow, FieldTarget target, FieldDefinition? existing)
    {
        _context = context;
        _useCases = useCases;
        _existing = existing;
        Target = target;
        _id = existing?.Id ?? new FieldId(Guid.NewGuid());
        KindChoices = [.. Enum.GetValues<FieldKind>().Select(k => context.Strings.GetString("FieldKind_" + k))];
        Options = [];
        if (existing is not null)
        {
            _stamp = WorkflowStamps.Field(workflow, existing.Id);
            Label = existing.Label.Display;
            Key = existing.Key.Display;
            KindIndex = (int)existing.Kind;
            IsRequired = existing.IsRequired;
            foreach (var option in existing.Options.OrderBy(o => o.Order))
            {
                Options.Add(NewOption(option.Id, option.Label.Display));
            }

            LoadDefault(existing.DefaultValue);
        }
        else
        {
            KindIndex = 0;
        }
    }

    public event EventHandler? CloseRequested;

    public FieldTarget Target { get; }

    public bool IsNew => _existing is null;

    public string DialogTitle => _context.Strings.GetString(IsNew
        ? (Target == FieldTarget.Task ? "FieldDialog_TitleNewTask" : "FieldDialog_TitleNewCr")
        : "FieldDialog_TitleEdit");

    public string PrimaryButtonText => _context.Strings.GetString(IsNew ? "FieldDialog_AddButton" : "FieldDialog_SaveButton");

    public string CloseButtonText => _context.Strings.GetString("Common_Cancel");

    public IReadOnlyList<string> KindChoices { get; }

    public ObservableCollection<FieldOptionRowViewModel> Options { get; }

    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsLookup), nameof(DefaultIsText), nameof(DefaultIsNumber), nameof(DefaultIsDate), nameof(DefaultIsLookup), nameof(HasNoDefault))]
    public partial int KindIndex { get; set; }

    [ObservableProperty]
    public partial bool IsRequired { get; set; }

    [ObservableProperty]
    public partial string DefaultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double DefaultNumber { get; set; } = double.NaN;

    [ObservableProperty]
    public partial DateTimeOffset? DefaultDate { get; set; }

    /// <summary>Index into <see cref="DefaultOptionChoices"/>; 0 is "None".</summary>
    [ObservableProperty]
    public partial int DefaultOptionIndex { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> DefaultOptionChoices { get; private set; } = [];

    [ObservableProperty]
    public partial string? LabelError { get; private set; }

    [ObservableProperty]
    public partial string? KeyError { get; private set; }

    [ObservableProperty]
    public partial string? OptionsError { get; private set; }

    [ObservableProperty]
    public partial string? DefaultError { get; private set; }

    [ObservableProperty]
    public partial string? RequiredError { get; private set; }

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    public bool CanSave => !IsSaving;

    public FieldKind Kind => KindIndex >= 0 && KindIndex < KindChoices.Count ? (FieldKind)KindIndex : FieldKind.Text;

    public bool IsLookup => Kind == FieldKind.Lookup;

    public bool DefaultIsText => Kind is FieldKind.Text or FieldKind.MultilineText or FieldKind.Url;

    public bool DefaultIsNumber => Kind == FieldKind.Number;

    public bool DefaultIsDate => Kind == FieldKind.Date;

    public bool DefaultIsLookup => Kind == FieldKind.Lookup;

    /// <summary>People and Tags fields start empty (interim: no default picker for them in release 1).</summary>
    public bool HasNoDefault => Kind is FieldKind.People or FieldKind.Tags;

    public static FieldDefinitionEditorViewModel ForNew(WorkspaceContext context, SettingsUseCases useCases, WorkflowSnapshot workflow, FieldTarget target) =>
        new(context, useCases, workflow, target, null);

    public static FieldDefinitionEditorViewModel ForExisting(WorkspaceContext context, SettingsUseCases useCases, WorkflowSnapshot workflow, FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new(context, useCases, workflow, field.Target, field);
    }

    public void AddOption()
    {
        Options.Add(NewOption(new FieldOptionId(Guid.NewGuid()), string.Empty));
        RefreshDefaultOptions();
    }

    public void RemoveOption(FieldOptionRowViewModel option)
    {
        if (Options.Remove(option))
        {
            RefreshDefaultOptions();
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (IsSaving)
        {
            return false;
        }

        if (_context.IsEditingBlocked)
        {
            SaveError = _context.Strings.GetString("Error_EditingBlocked");
            return false;
        }

        ClearErrors();
        if (!TryBuildDefault(out var defaultValue))
        {
            return false;
        }

        var options = IsLookup ? Options.Select(o => new FieldOptionDraft(o.Id, o.Label)).ToList() : [];
        var draft = new FieldDefinitionDraft(Label, IsRequired, defaultValue, options);
        var operation = _retryOperation ?? OperationId.New();
        IsSaving = true;
        _context.SaveStatus.Begin();
        var result = _existing is null
            ? await _useCases.AddFieldDefinition.ExecuteAsync(new AddFieldDefinitionCommand(operation, _id, Target, Key, Kind, draft), CancellationToken.None).ConfigureAwait(true)
            : await _useCases.UpdateFieldDefinition.ExecuteAsync(new UpdateFieldDefinitionCommand(operation, _id, _stamp, draft), CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            var failure = CommandFailure.From(result.Error!, operation, "Record_Settings", _context.Strings);
            _context.SaveStatus.Report(failure);
            _retryOperation = failure.RequiresSameOperationOnRetry ? operation : null;
            IsSaving = false;
            ShowFailure(result.Error!, failure.Message);
            _context.Announce(failure.Message, isFailure: true);
            return false;
        }

        _context.SaveStatus.Complete();
        _retryOperation = null;
        await _context.WhenDisplayedAsync(result.Value.CommitSequence, DisplayTimeout).ConfigureAwait(true);
        IsSaving = false;
        _context.Announce(_context.Strings.GetString("SaveStatus_Saved"));
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    partial void OnLabelChanged(string value)
    {
        // Design §4.6: the key is proposed from the label until the user edits it, and only before the first save.
        if (IsNew && !_keyEdited)
        {
            _isProposingKey = true;
            Key = ProposeKey(value);
            _isProposingKey = false;
        }
    }

    partial void OnKeyChanged(string value)
    {
        if (!_isProposingKey)
        {
            _keyEdited = true;
        }
    }

    partial void OnKindIndexChanged(int value) => RefreshDefaultOptions();

    private static string ProposeKey(string label)
    {
        var builder = new StringBuilder();
        foreach (var c in label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var key = builder.ToString().Trim('_');
        while (key.Length > 0 && !char.IsAsciiLetter(key[0]))
        {
            key = key[1..];
        }

        return key.Length > DomainLimits.FieldKey ? key[..DomainLimits.FieldKey] : key;
    }

    private FieldOptionRowViewModel NewOption(FieldOptionId id, string label)
    {
        var option = new FieldOptionRowViewModel(id, label, _context.Strings.GetString("FieldDialog_RemoveOption"));
        option.PropertyChanged += (_, _) => RefreshDefaultOptions();
        return option;
    }

    private void RefreshDefaultOptions()
    {
        var selected = DefaultOptionIndex;
        DefaultOptionChoices = [_context.Strings.GetString("Common_None"), .. Options.Select(o => o.Label)];
        DefaultOptionIndex = selected >= 0 && selected < DefaultOptionChoices.Count ? selected : 0;
    }

    private void LoadDefault(CustomValue? value)
    {
        RefreshDefaultOptions();
        switch (value)
        {
            case TextCustomValue text:
                DefaultText = text.Value;
                break;
            case MultilineTextCustomValue text:
                DefaultText = text.Value;
                break;
            case UrlCustomValue url:
                DefaultText = url.Url.ToString();
                break;
            case NumberCustomValue number:
                DefaultNumber = (double)number.Value;
                break;
            case DateCustomValue date:
                DefaultDate = DraftDates.ToPicker(date.Value);
                break;
            case LookupCustomValue lookup:
                var index = Options.ToList().FindIndex(o => o.Id == lookup.OptionId);
                DefaultOptionIndex = index >= 0 ? index + 1 : 0;
                break;
            default:
                break;
        }
    }

    private bool TryBuildDefault(out CustomValue? value)
    {
        value = null;
        switch (Kind)
        {
            case FieldKind.Text when !string.IsNullOrWhiteSpace(DefaultText):
                value = new TextCustomValue(DefaultText.Trim());
                return true;
            case FieldKind.MultilineText when !string.IsNullOrWhiteSpace(DefaultText):
                value = new MultilineTextCustomValue(DefaultText);
                return true;
            case FieldKind.Url when !string.IsNullOrWhiteSpace(DefaultText):
                var url = HttpUrl.CreateOptional(DefaultText, "DefaultValue");
                if (!url.IsSuccess || url.Value is null)
                {
                    DefaultError = _context.Strings.GetString("Error_InvalidUrl");
                    return false;
                }

                value = new UrlCustomValue(url.Value);
                return true;
            case FieldKind.Number when !double.IsNaN(DefaultNumber):
                if (double.IsInfinity(DefaultNumber) || Math.Abs(DefaultNumber) > (double)decimal.MaxValue)
                {
                    DefaultError = _context.Strings.Format("Error_Invalid", _context.Strings.GetString("Field_DefaultValue"));
                    return false;
                }

                value = new NumberCustomValue((decimal)DefaultNumber);
                return true;
            case FieldKind.Date when DraftDates.FromPicker(DefaultDate) is { } day:
                value = new DateCustomValue(day);
                return true;
            case FieldKind.Lookup when DefaultOptionIndex > 0 && DefaultOptionIndex <= Options.Count:
                value = new LookupCustomValue(Options[DefaultOptionIndex - 1].Id);
                return true;
            default:
                return true;
        }
    }

    private void ShowFailure(AppError error, string message)
    {
        if (error is not ValidationFailedError validation)
        {
            SaveError = message;
            return;
        }

        foreach (var domainError in validation.Errors)
        {
            var text = CommandErrorText.Describe(domainError, _context.Strings);
            switch (domainError.Field)
            {
                case "Label":
                    LabelError = text;
                    break;
                case "Key":
                    KeyError = text;
                    break;
                case "Options":
                    OptionsError = text;
                    break;
                case "DefaultValue":
                    DefaultError = text;
                    break;
                case "IsRequired":
                    RequiredError = text;
                    break;
                default:
                    SaveError = text;
                    break;
            }
        }
    }

    private void ClearErrors() => LabelError = KeyError = OptionsError = DefaultError = RequiredError = SaveError = null;

    /// <summary>The kind label for the read-only kind of an existing field.</summary>
    public string KindText => KindChoices[(int)Kind];
}
