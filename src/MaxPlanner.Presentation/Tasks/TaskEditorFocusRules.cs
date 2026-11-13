namespace MaxPlanner.Presentation.Tasks;

/// <summary>The task editor's focus decisions, kept free of WinUI so tests can check them.</summary>
public static class TaskEditorFocusRules
{
    /// <summary>
    /// The field key that takes the dialog's first focus: the title when the user can edit it, otherwise the first
    /// editable field (Service). With nothing editable, the shown read-only title box (still readable and copyable);
    /// null when a CR supplies the title too, which leaves the dialog's own choice.
    /// </summary>
    /// <param name="isTitleEditable">The task's own title is shown and editable.</param>
    /// <param name="canEditFields">The fields are editable (not archived, deleted elsewhere or saving).</param>
    /// <param name="isTitleBoxShown">The title box is shown (no linked or link-pending CR supplies the title).</param>
    public static string? FirstFieldKey(bool isTitleEditable, bool canEditFields, bool isTitleBoxShown) =>
        isTitleEditable ? "Title"
        : canEditFields ? "Service"
        : isTitleBoxShown ? "Title"
        : null;
}
