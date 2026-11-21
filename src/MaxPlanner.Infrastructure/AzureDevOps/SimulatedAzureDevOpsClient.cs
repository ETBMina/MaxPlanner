#if DEBUG
using MaxPlanner.Application.PullRequests;

namespace MaxPlanner.Infrastructure.AzureDevOps;

/// <summary>
/// The Debug-only Azure DevOps simulator (ADR 0031) for testing pull request creation without a server. Each call waits
/// a short time, honors cancellation, and answers with the result the real <see cref="AzureDevOpsClient"/> would give.
/// Pull requests live in memory for the app session, with IDs from 9001. Cases, matched without case:
/// source <c>dup-</c> already exists, <c>empty-</c> has no new commits, <c>gone-</c> is not found, <c>slow-</c> times out
/// on create, <c>lost-</c> loses the connection after the create was recorded, <c>fail-</c> gets HTTP 500 on create;
/// a target <c>gone-</c> is not found; token <c>bad</c> is refused on every call; project <c>nope</c> is not found.
/// </summary>
public sealed class SimulatedAzureDevOpsClient : IAzureDevOpsClient
{
    public const int FirstPullRequestId = 9001;

    private const string ExistsMessage = "TF401179: An active pull request for the source and target branch already exists.";

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Source, string Target), int> _pullRequests = [];
    private readonly TimeSpan _minDelay;
    private readonly TimeSpan _maxDelay;
    private int _nextId = FirstPullRequestId;

    /// <param name="minDelay">Shortest wait per call; null is 400 ms. Tests pass zero.</param>
    /// <param name="maxDelay">Longest wait per call; null is 800 ms.</param>
    public SimulatedAzureDevOpsClient(TimeSpan? minDelay = null, TimeSpan? maxDelay = null)
    {
        _minDelay = minDelay ?? TimeSpan.FromMilliseconds(400);
        _maxDelay = maxDelay ?? TimeSpan.FromMilliseconds(800);
    }

    public async Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken)
    {
        // connectionData is collection-level, so an unknown project does not affect it.
        return await GuardAsync(connection, checkProject: false, cancellationToken).ConfigureAwait(false) is { } failure
            ? AzureDevOpsResult.Failed<AzureDevOpsUser>(failure)
            : AzureDevOpsResult.Success(new AzureDevOpsUser("simulator-user", "Simulator User"));
    }

    public async Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        await GuardAsync(connection, checkProject: true, cancellationToken).ConfigureAwait(false) is { } failure
            ? AzureDevOpsResult.Failed<string>(failure)
            : AzureDevOpsResult.Success(connection.Repository);

    public async Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken) =>
        await GuardAsync(connection, checkProject: true, cancellationToken).ConfigureAwait(false) is { } failure
            ? AzureDevOpsResult.Failed<bool>(failure)
            : AzureDevOpsResult.Success(!Is(branch, "gone-"));

    public async Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        await GuardAsync(connection, checkProject: true, cancellationToken).ConfigureAwait(false) is { } failure
            ? AzureDevOpsResult.Failed<int>(failure)
            : AzureDevOpsResult.Success(Is(source, "empty-") ? 0 : 3);

    public async Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);

        // A timeout takes longer than a normal call, so the Creating step stays visible.
        var slow = Is(source, "slow-");
        if (await GuardAsync(connection, checkProject: true, cancellationToken, slow ? 3 : 1).ConfigureAwait(false) is { } failure)
        {
            return AzureDevOpsResult.Failed<int>(failure);
        }

        if (slow)
        {
            return AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout));
        }

        if (Is(source, "fail-"))
        {
            return AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 500, "Simulated server error."));
        }

        lock (_gate)
        {
            var key = Key(source, target);
            if (Is(source, "dup-"))
            {
                SeedLocked(key);
            }

            if (_pullRequests.ContainsKey(key))
            {
                return AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.AlreadyExists, 409, ExistsMessage));
            }

            var id = _nextId++;
            _pullRequests[key] = id;

            // The request reached the server and was recorded, but the answer never arrived.
            return Is(source, "lost-")
                ? AzureDevOpsResult.Failed<int>(new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown))
                : AzureDevOpsResult.Success(id);
        }
    }

    public async Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        return await GuardAsync(connection, checkProject: true, cancellationToken).ConfigureAwait(false) is { } failure
            ? AzureDevOpsResult.Failed<bool>(failure)
            : AzureDevOpsResult.Success(true);
    }

    public async Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken)
    {
        if (await GuardAsync(connection, checkProject: true, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return AzureDevOpsResult.Failed<int?>(failure);
        }

        lock (_gate)
        {
            var key = Key(source, target);
            if (Is(source, "dup-"))
            {
                SeedLocked(key);
            }

            return AzureDevOpsResult.Success<int?>(_pullRequests.TryGetValue(key, out var id) ? id : null);
        }
    }

    // The pre-existing pull request a "dup-" source always has.
    private void SeedLocked((string Source, string Target) key)
    {
        if (!_pullRequests.ContainsKey(key))
        {
            _pullRequests[key] = _nextId++;
        }
    }

    // Waits like a server call, then applies the token and project cases every call shares.
    private async Task<AzureDevOpsFailure?> GuardAsync(AzureDevOpsConnection connection, bool checkProject, CancellationToken cancellationToken, int delayFactor = 1)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var delay = _minDelay + ((_maxDelay - _minDelay) * Random.Shared.NextDouble());
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay * delayFactor, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(connection.Token, "bad", StringComparison.Ordinal))
        {
            return new AzureDevOpsFailure(AzureDevOpsFailureKind.Unauthorized, 401);
        }

        return checkProject && string.Equals(connection.Project.Trim(), "nope", StringComparison.OrdinalIgnoreCase)
            ? new AzureDevOpsFailure(AzureDevOpsFailureKind.NotFound, 404, "TF200016: The following project does not exist: nope.")
            : null;
    }

    private static bool Is(string branch, string prefix) => BranchNames.ToShortName(branch).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static (string Source, string Target) Key(string source, string target) =>
        (BranchNames.ToShortName(source).ToUpperInvariant(), BranchNames.ToShortName(target).ToUpperInvariant());
}
#endif
