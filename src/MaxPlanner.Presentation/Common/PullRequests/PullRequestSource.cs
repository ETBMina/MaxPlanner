using MaxPlanner.Application.PullRequests;
using MaxPlanner.Application.ReadModels;

namespace MaxPlanner.Presentation.Common.PullRequests;

/// <summary>
/// The saved values a pull request is created from (design §5.11): the record's own title for the dialog's name, and the
/// CR number, task number and title for the branch default and the title template. A Subtask card uses its parent's.
/// </summary>
public sealed record PullRequestSource(string RecordTitle, string? CrNumber, string? TaskNumber, string? Title)
{
    public static PullRequestSource From(TaskReadModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new(task.EffectiveTitle, task.Cr.CrNumber, task.Cr.TaskNumber, task.EffectiveTitle);
    }

    /// <summary>B-16.2: the CR values inherited through the parent, and the parent's title.</summary>
    public static PullRequestSource From(SubtaskCardReadModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return new(card.Subtitle, card.Cr.CrNumber, card.Cr.TaskNumber, card.ParentTitle);
    }

    public static PullRequestSource From(ChangeRequestReadModel changeRequest)
    {
        ArgumentNullException.ThrowIfNull(changeRequest);
        return new(changeRequest.Title, changeRequest.Number, changeRequest.TaskNumber, changeRequest.Title);
    }

    public PullRequestTitleValues TitleValues(string? source, string? target) =>
        new(CrNumber, TaskNumber, Title, BranchNames.ToShortName(source), BranchNames.ToShortName(target));
}

/// <summary>
/// The Azure DevOps ports the pull request dialog and Settings use (ADR 0031): the REST client and the Credential
/// Manager token store. <see cref="Unavailable"/> is used where the composition gave none (tests without them).
/// </summary>
public sealed record AzureDevOpsServices(IAzureDevOpsClient Client, IAzureDevOpsTokenStore Tokens)
{
    public static AzureDevOpsServices Unavailable { get; } = new(new NoClient(), new NoTokens());

    private sealed class NoTokens : IAzureDevOpsTokenStore
    {
        public string? Read() => null;

        public void Save(string token)
        {
        }

        public void Remove()
        {
        }
    }

    private sealed class NoClient : IAzureDevOpsClient
    {
        private static readonly AzureDevOpsFailure Failure = new(AzureDevOpsFailureKind.Network);

        public Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) => Fail<AzureDevOpsUser>();

        public Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) => Fail<string>();

        public Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken) => Fail<bool>();

        public Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) => Fail<int>();

        public Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken) => Fail<int>();

        public Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken) => Fail<bool>();

        public Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) => Fail<int?>();

        private static Task<AzureDevOpsResult<T>> Fail<T>() => Task.FromResult(AzureDevOpsResult.Failed<T>(Failure));
    }
}
