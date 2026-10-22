using System.Globalization;

namespace GridGate;

public sealed record SaveResult(bool IsSuccess, string? ErrorMessage)
{
    public static SaveResult Success { get; } = new(true, null);

    public static SaveResult Failure(string message) => new(false, message);
}

/// <summary>Stand-in for the application use case that validates and persists one cell edit.</summary>
public interface ISaveService
{
    /// <summary>Persists one field. Completes on a thread-pool thread; callers marshal back to the UI themselves.</summary>
    Task<SaveResult> SaveAsync(Guid rowId, string field, object? value, CancellationToken cancellationToken = default);
}

public sealed class FakeSaveService : ISaveService
{
    private static readonly TimeSpan s_latency = TimeSpan.FromMilliseconds(300);

    public async Task<SaveResult> SaveAsync(Guid rowId, string field, object? value, CancellationToken cancellationToken = default)
    {
        await Task.Delay(s_latency, cancellationToken).ConfigureAwait(false);

        var text = value switch
        {
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            null => string.Empty,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        return text.Contains("fail", StringComparison.OrdinalIgnoreCase)
            ? SaveResult.Failure($"Couldn't save {field}: simulated storage error (value contains \"fail\").")
            : SaveResult.Success;
    }
}
