using MaxPlanner.Application.Abstractions;

namespace MaxPlanner.Application.Common;

internal static class UseCaseResults
{
    /// <summary>A failure found before the transaction (draft validation); the coordinator is not called.</summary>
    public static Task<AppResult<CommandOutcome<T>>> Failed<T>(AppError error) => Task.FromResult(AppResult.Failure<CommandOutcome<T>>(error));
}
