using MaxPlanner.Application.PullRequests;

namespace MaxPlanner.Application.Tests.Fakes;

/// <summary>
/// A scripted Azure DevOps server: existing branches, commits ahead, and a failure per call when set. It records every
/// call by name, so a test can assert that nothing was created or retried.
/// </summary>
internal sealed class FakeAzureDevOpsClient : IAzureDevOpsClient
{
    public HashSet<string> Branches { get; } = new(StringComparer.OrdinalIgnoreCase) { "12345", "uat" };

    public int CommitsAhead { get; set; } = 3;

    public int CreatedId { get; set; } = 42;

    public int? ActivePullRequest { get; set; }

    public Dictionary<string, AzureDevOpsFailure> Failures { get; } = [];

    public List<string> Calls { get; } = [];

    public string? AutoCompleteUserId { get; private set; }

    public (string Source, string Target, string Title)? Created { get; private set; }

    public Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        Answer(nameof(GetUserAsync), () => new AzureDevOpsUser("user-1", "Max"));

    public Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        Answer(nameof(GetRepositoryAsync), () => connection.Repository);

    public Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken) =>
        Answer(nameof(BranchExistsAsync), () => Branches.Contains(BranchNames.ToShortName(branch)));

    public Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        Answer(nameof(GetCommitsAheadAsync), () => CommitsAhead);

    public Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken) =>
        Answer(nameof(CreatePullRequestAsync), () =>
        {
            Created = (source, target, title);
            return CreatedId;
        });

    public Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken) =>
        Answer(nameof(SetAutoCompleteAsync), () =>
        {
            AutoCompleteUserId = userId;
            return true;
        });

    public Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        Answer(nameof(FindActivePullRequestAsync), () => ActivePullRequest);

    private Task<AzureDevOpsResult<T>> Answer<T>(string call, Func<T> value)
    {
        Calls.Add(call);
        return Task.FromResult(Failures.TryGetValue(call, out var failure) ? AzureDevOpsResult.Failed<T>(failure) : AzureDevOpsResult.Success(value()));
    }
}
