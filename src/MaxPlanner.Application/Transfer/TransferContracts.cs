using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer.External;

namespace MaxPlanner.Application.Transfer;

/// <summary>The T-10 design-default limits. The UI and every reader enforce the same values; nothing is truncated.</summary>
public static class TransferLimits
{
    /// <summary>25 MiB per input file.</summary>
    public const long MaxInputBytes = 25L * 1024 * 1024;

    /// <summary>Top-level business records per import (tasks, Subtask cards, CRs, reviews, General to-dos).</summary>
    public const int MaxTopLevelRecords = 50_000;

    public const int MaxJsonDepth = 64;

    /// <summary>200 MiB of expanded XLSX/ZIP content, checked before any entry is parsed.</summary>
    public const long MaxExpandedBytes = 200L * 1024 * 1024;

    /// <summary>Entries in one XLSX or CSV bundle; a workbook of seven sheets needs far fewer.</summary>
    public const int MaxArchiveEntries = 1_000;

    /// <summary>Markdown files in one folder import.</summary>
    public const int MaxMarkdownFiles = 10_000;
}

public enum TransferFileFormat
{
    Json,
    Xlsx,

    /// <summary>One CSV file holding one table (B-12).</summary>
    CsvTable,

    /// <summary>A ZIP of one CSV per table plus <c>metadata.json</c> (B-12).</summary>
    CsvBundle,
}

/// <summary>The tables of the tabular formats; each is one XLSX worksheet or one CSV file (B-12).</summary>
public enum TransferTable
{
    Tasks,
    ChangeRequests,
    ChecklistItems,
    SubtaskCards,
    Reviews,
    GeneralTodos,
}

public enum TransferFailure
{
    /// <summary>The input exceeds a T-10 limit (size, records, depth, expanded content).</summary>
    TooLarge,

    /// <summary>The file cannot be parsed in the declared format.</summary>
    Malformed,

    /// <summary>A newer <c>formatVersion</c> than this app reads (B-12).</summary>
    UnsupportedVersion,

    /// <summary>A ZIP entry escapes the archive, is unexpected, or the archive is otherwise unsafe (B-12 CSV, T-10).</summary>
    UnsafeArchive,

    /// <summary>A spreadsheet cell holds a formula; formulas are never evaluated (T-10).</summary>
    FormulaRejected,

    /// <summary>The file could not be read or written.</summary>
    Io,

    /// <summary>The content does not fit a spreadsheet cell; JSON has no such limit (B-12).</summary>
    CellLimitExceeded,
}

/// <summary>A transfer file could not be read or written (T-10). Nothing was imported or replaced.</summary>
/// <param name="Detail">A location such as a sheet, row, or entry name; never cell or note content.</param>
public sealed record TransferFileError(TransferFailure Failure, string? Detail = null) : AppError
{
    public override string Code => "transfer-file";
}

/// <summary>
/// The dataset changed after the import preview (T-10: revisions and references are revalidated at commit). Nothing
/// was written; the preview must be prepared again.
/// </summary>
public sealed record ImportPlanStaleError(string Reason) : AppError
{
    public override string Code => "import-plan-stale";
}

/// <summary>The preview has errors or nothing to apply, so the plan cannot be committed.</summary>
public sealed record ImportNotApplicableError : AppError
{
    public static ImportNotApplicableError Instance { get; } = new();

    public override string Code => "import-not-applicable";
}

/// <summary>Writes a transfer file (Infrastructure). The destination is replaced only after a complete write (T-10).</summary>
public interface ITransferFileWriter
{
    /// <param name="table">The table of a <see cref="TransferFileFormat.CsvTable"/> export; ignored otherwise.</param>
    /// <returns>The written path, or a <see cref="TransferFileError"/> / <see cref="OperationCancelledError"/>.</returns>
    /// <remarks>Runs off the calling thread. Cancellation or failure leaves an existing file at the destination unchanged.</remarks>
    Task<AppResult<string>> WriteAsync(TransferDocument document, TransferFileFormat format, TransferTable? table, string destinationPath, CancellationToken cancellationToken);
}

/// <summary>Reads a Max Planner transfer file (JSON, XLSX, CSV table, or CSV bundle) without writing anything.</summary>
public interface ITransferFileReader
{
    /// <param name="table">For a single CSV file: the table it holds; null to detect it from the headers.</param>
    /// <returns>The document, or a <see cref="TransferFileError"/> / <see cref="OperationCancelledError"/>.</returns>
    Task<AppResult<TransferDocument>> ReadAsync(string path, TransferTable? table, CancellationToken cancellationToken);
}

/// <summary>Reads user-selected external sources read-only (B-13): CSV, XLSX, simple JSON, and Markdown folders.</summary>
public interface IExternalSourceReader
{
    /// <param name="paths">Files, or one folder of Markdown records.</param>
    /// <param name="generalChecklistPath">An optional Markdown file whose checkboxes become General to-dos (B-13.3).</param>
    Task<AppResult<ExternalSource>> ReadAsync(IReadOnlyList<string> paths, string? generalChecklistPath, CancellationToken cancellationToken);
}

/// <summary>
/// Bookkeeping an import preview needs beyond the read snapshot, read on a pooled reader: the B-13.5 import ledger
/// and the B-05 tombstones (a deleted record must not reappear through an import).
/// </summary>
public interface IImportStateReader
{
    /// <returns>Record IDs by (entity kind, source key) of one source namespace, or a <see cref="StorageFailureError"/>.</returns>
    Task<AppResult<IReadOnlyDictionary<(string EntityKind, string SourceKey), Guid>>> LoadLedgerAsync(string sourceNamespace, CancellationToken cancellationToken);

    /// <returns>The IDs among <paramref name="ids"/> that a tombstone marks as deleted, or a <see cref="StorageFailureError"/>.</returns>
    Task<AppResult<IReadOnlySet<Guid>>> FindDeletedAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
