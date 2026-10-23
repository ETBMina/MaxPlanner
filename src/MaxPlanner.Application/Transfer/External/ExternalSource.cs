namespace MaxPlanner.Application.Transfer.External;

// B-13: what the external readers produce. Sources are read-only and carry no stable IDs; the wizard maps their
// columns explicitly and the import ledger supplies identities on repeated imports.

public enum ExternalSourceKind
{
    Csv,
    Xlsx,

    /// <summary>A simple JSON envelope <c>{ "tasks": [...], "crs": [...], "reviews": [...] }</c> (B-13.1).</summary>
    Json,

    /// <summary>A folder of Markdown records with YAML front matter (B-13.3).</summary>
    Markdown,
}

/// <summary>One cell: text, or a list for JSON/YAML arrays (people, tags, services). Spreadsheet dates arrive as ISO text.</summary>
public sealed record ExternalValue(string? Text, IReadOnlyList<string>? Items = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && (Items is null || Items.Count == 0);
}

/// <summary>A Markdown checkbox (B-13.3).</summary>
public sealed record ExternalChecklistItem(string Text, bool IsDone);

/// <param name="SourceKey">The record's identity in its source (an id column, a front-matter id, or the file and row).</param>
/// <param name="RowNumber">1-based data row (spreadsheet rows count from the header) or file index, for findings.</param>
/// <param name="Values">Aligned with <see cref="ExternalTable.Columns"/>; null where a row has no value.</param>
/// <param name="Checklist">Markdown task records: checkboxes under a "Subtasks" or "Checklist" heading.</param>
/// <param name="Body">Markdown: the remaining body text, without the imported checklist lines (B-13.3).</param>
public sealed record ExternalRow(
    string SourceKey,
    int RowNumber,
    IReadOnlyList<ExternalValue?> Values,
    IReadOnlyList<ExternalChecklistItem>? Checklist = null,
    string? Body = null);

/// <param name="Name">The file, sheet, or JSON collection name, shown in the wizard.</param>
/// <param name="SuggestedEntity">The entity the reader detected (JSON collection, Markdown <c>type</c>); null when unknown.</param>
public sealed record ExternalTable(string Name, ExternalSourceKind Kind, ExternalEntity? SuggestedEntity, IReadOnlyList<string> Columns, IReadOnlyList<ExternalRow> Rows);

/// <summary>A checkbox of the separately selected General checklist file (B-13.3).</summary>
public sealed record ExternalTodo(string SourceKey, string Text, bool IsDone);

public sealed record ExternalSource(IReadOnlyList<ExternalTable> Tables, IReadOnlyList<ExternalTodo> GeneralTodos);

/// <summary>The entity type a source table holds (B-13.1: the wizard identifies it).</summary>
public enum ExternalEntity
{
    Task,
    ChangeRequest,
    Review,
    GeneralTodo,

    /// <summary>The table is not imported.</summary>
    Ignore,
}
