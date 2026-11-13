namespace MaxPlanner.Presentation.Grid;

/// <summary>The editor a Grid flyout shows (design §4.3).</summary>
public enum GridFlyoutEditorKind
{
    /// <summary>A multi-line text box: Notes, and any flyout column without a picker draft.</summary>
    Notes,

    /// <summary>The people or tag picker.</summary>
    TokenPicker,

    /// <summary>The custom field's own editor.</summary>
    CustomField,
}
