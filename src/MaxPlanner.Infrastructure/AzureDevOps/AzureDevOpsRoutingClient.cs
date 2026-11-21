#if DEBUG
using MaxPlanner.Application.PullRequests;

namespace MaxPlanner.Infrastructure.AzureDevOps;

/// <summary>
/// Debug only (ADR 0031): sends calls whose collection URL host is <c>simulator</c> to the simulator, all others to
/// the real client.
/// </summary>
public sealed class AzureDevOpsRoutingClient(IAzureDevOpsClient real, IAzureDevOpsClient simulator) : IAzureDevOpsClient
{
    private readonly IAzureDevOpsClient _real = real ?? throw new ArgumentNullException(nameof(real));
    private readonly IAzureDevOpsClient _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));

    public Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        For(connection).GetUserAsync(connection, cancellationToken);

    public Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        For(connection).GetRepositoryAsync(connection, cancellationToken);

    public Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken) =>
        For(connection).BranchExistsAsync(connection, branch, cancellationToken);

    public Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        For(connection).GetCommitsAheadAsync(connection, source, target, cancellationToken);

    public Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken) =>
        For(connection).CreatePullRequestAsync(connection, source, target, title, cancellationToken);

    public Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken) =>
        For(connection).SetAutoCompleteAsync(connection, pullRequestId, userId, cancellationToken);

    public Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        For(connection).FindActivePullRequestAsync(connection, source, target, cancellationToken);

    private IAzureDevOpsClient For(AzureDevOpsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return AzureDevOpsSimulator.IsSimulatorHost(connection.CollectionUrl) ? _simulator : _real;
    }
}
#endif
