#if DEBUG
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;

namespace MaxPlanner.Desktop.Composition;

/// <summary>
/// Debug builds only, and only when <c>MAXPLANNER_DEBUG_HOOKS=1</c>: lets a UI evidence run hold or fail the
/// next command without keyboard input or a real full disk. A test script drops a trigger file into
/// <c>data/debug/</c>; each trigger applies to exactly one command and is deleted when used.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>delay-next-commit</c> (contents: milliseconds) waits before the command reaches the writer, so the
/// cell's saving state can be captured, and other commands can commit first and make it genuinely stale.</item>
/// <item><c>fail-next-commit</c> returns <see cref="StorageFailureError"/> (DiskFull) without writing, the
/// typed result the SQLite coordinator returns for SQLITE_FULL. The real SQLITE_FULL path is covered by
/// <c>GridEditPathTests</c> on a real database file.</item>
/// </list>
/// Release builds do not compile this class (Phase 2 evidence, docs/evidence/phase-2).
/// </remarks>
internal sealed class DebugCommitHooks(IMutationCoordinator inner, string dataDirectory) : IMutationCoordinator
{
    private readonly string _triggers = Path.Combine(dataDirectory, "debug");

    public static bool IsEnabled => Environment.GetEnvironmentVariable("MAXPLANNER_DEBUG_HOOKS") == "1";

    public async Task<AppResult<CommandOutcome<T>>> ExecuteAsync<T>(
        OperationId operationId,
        string operationKind,
        Func<IWriteTransaction, TxResult<T>> work,
        CancellationToken cancellationToken)
    {
        if (TryTake("delay-next-commit", out var delay) && int.TryParse(delay, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }

        if (TryTake("fail-next-commit", out _))
        {
            return AppResult.Failure<CommandOutcome<T>>(new StorageFailureError(StorageFailureKind.DiskFull));
        }

        return await inner.ExecuteAsync(operationId, operationKind, work, cancellationToken).ConfigureAwait(false);
    }

    private bool TryTake(string name, out string contents)
    {
        var path = Path.Combine(_triggers, name);
        contents = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            contents = File.ReadAllText(path).Trim();
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
#endif
