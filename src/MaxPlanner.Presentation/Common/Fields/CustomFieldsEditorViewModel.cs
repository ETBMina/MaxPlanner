using MaxPlanner.Application.Workflow;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.Fields;

/// <summary>
/// The custom fields of one editor (B-11): one typed editor per active field of the target, in display order.
/// Values of deactivated fields are never shown and are carried over unchanged on save, so they stay in
/// exports and backups. Use on the UI thread; the editor is built once per dialog from the workflow it opened with.
/// </summary>
public sealed class CustomFieldsEditorViewModel
{
    private readonly WorkflowSnapshot _workflow;

    public CustomFieldsEditorViewModel(FieldTarget target, WorkflowSnapshot workflow, ILocalizedStrings strings)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        ArgumentNullException.ThrowIfNull(strings);
        Fields = [.. workflow.FieldsFor(target)
            .Where(f => f.IsActive)
            .OrderBy(f => f.DisplayOrder)
            .ThenBy(f => f.Id.Value)
            .Select(f => new CustomFieldEditorViewModel(f, workflow, strings))];
        foreach (var field in Fields)
        {
            field.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when the user changed any value.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<CustomFieldEditorViewModel> Fields { get; }

    public bool HasFields => Fields.Count > 0;

    /// <summary>A comparable form of every draft value, for dirty tracking.</summary>
    public string Signature => string.Join('\u001f', Fields.Select(f => f.Signature));

    /// <summary>The <see cref="Signature"/> of stored values, as the draft would show them once loaded.</summary>
    public string SignatureOf(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join('', Fields.Select(f => CustomFieldEditorViewModel.SignatureOf(f.Field, values.GetValueOrDefault(f.Field.Id))));
    }

    /// <summary>The draft values as text, for the conflict review (design §5.10).</summary>
    public string DisplayText(string none) =>
        Fields.Count == 0 ? none : string.Join("; ", Fields.Select(f => f.Field.Label.Display + ": " + (f.DisplayText.Length == 0 ? none : f.DisplayText)));

    public bool IsReadOnly
    {
        set
        {
            foreach (var editor in Fields)
            {
                editor.IsReadOnly = value;
            }
        }
    }

    /// <summary>Shows a record's stored values.</summary>
    public void Load(IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var field in Fields)
        {
            field.Load(values.GetValueOrDefault(field.Field.Id), _workflow);
        }
    }

    /// <summary>A new record starts with each field's default value (B-11).</summary>
    public void LoadDefaults()
    {
        foreach (var field in Fields)
        {
            field.Load(field.Field.DefaultValue, _workflow);
        }
    }

    /// <summary>
    /// The values to send: the drafts of the active fields (empty ones omitted) plus the stored values of every
    /// other field. False when a draft cannot be converted; its field then shows the error.
    /// </summary>
    public bool TryBuild(IReadOnlyDictionary<FieldId, CustomValue> stored, out IReadOnlyDictionary<FieldId, CustomValue> values)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var shown = Fields.Select(f => f.Field.Id).ToHashSet();
        var result = stored.Where(v => !shown.Contains(v.Key)).ToDictionary();
        var valid = true;
        foreach (var field in Fields)
        {
            if (!field.TryGetValue(out var value))
            {
                valid = false;
            }
            else if (value is not null)
            {
                result[field.Field.Id] = value;
            }
        }

        values = result.AsReadOnly();
        return valid;
    }

    /// <summary>Shows the use case's errors for custom fields (<c>Custom:{key}</c>) beside their editors.</summary>
    /// <returns>The key of the first field that got an error, or null when none matched.</returns>
    public string? ShowErrors(IEnumerable<DomainError> errors, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(strings);
        string? first = null;
        foreach (var error in errors)
        {
            if (Fields.FirstOrDefault(f => error.Field == f.Field.ErrorField) is not { } field)
            {
                continue;
            }

            // The generic field text uses the field's own label rather than a "Field_*" resource.
            field.Error = error.Code switch
            {
                DomainErrorCodes.Required => strings.Format("Error_Required", field.Field.Label.Display),
                DomainErrorCodes.TooLong => strings.Format("Error_TooLong", field.Field.Label.Display, error.Limit),
                DomainErrorCodes.UnknownReference or DomainErrorCodes.UnknownLookupOption => strings.Format("Error_UnknownReference", field.Field.Label.Display),
                _ => strings.Format("Error_Invalid", field.Field.Label.Display),
            };
            first ??= field.Key;
        }

        return first;
    }

    /// <summary>The key of the first field with a conversion error from <see cref="TryBuild"/>.</summary>
    public string? FirstErrorKey => Fields.FirstOrDefault(f => f.Error is not null)?.Key;

    public void ClearErrors()
    {
        foreach (var field in Fields)
        {
            field.Error = null;
        }
    }
}
