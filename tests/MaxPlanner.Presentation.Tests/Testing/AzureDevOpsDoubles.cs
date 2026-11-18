using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common.PullRequests;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Tests.Testing;

/// <summary>A scripted Azure DevOps server for the dialog and Settings view models; it records every call.</summary>
internal sealed class FakeAzureDevOpsClient : IAzureDevOpsClient
{
    public HashSet<string> Branches { get; } = new(StringComparer.OrdinalIgnoreCase) { "12345", "uat", "0042" };

    public int CommitsAhead { get; set; } = 2;

    public int CreatedId { get; set; } = 1187;

    public int? ActivePullRequest { get; set; }

    public Dictionary<string, AzureDevOpsFailure> Failures { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>When set, the user call waits for it, so a test can look at the dialog while a request runs.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        return Answer(nameof(GetUserAsync), () => new AzureDevOpsUser("user-1", "Max"));
    }

    public Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(GetRepositoryAsync), () => connection.Repository));

    public Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(BranchExistsAsync), () => Branches.Contains(BranchNames.ToShortName(branch))));

    public Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(GetCommitsAheadAsync), () => CommitsAhead));

    public Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(CreatePullRequestAsync), () => CreatedId));

    public Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(SetAutoCompleteAsync), () => true));

    public Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken) =>
        Task.FromResult(Answer(nameof(FindActivePullRequestAsync), () => ActivePullRequest));

    private AzureDevOpsResult<T> Answer<T>(string call, Func<T> value)
    {
        Calls.Add(call);
        return Failures.TryGetValue(call, out var failure) ? AzureDevOpsResult.Failed<T>(failure) : AzureDevOpsResult.Success(value());
    }
}

internal sealed class FakeTokenStore(string? token = "pat") : IAzureDevOpsTokenStore
{
    public string? Token { get; private set; } = token;

    /// <summary>When set, saving throws it, like a Credential Manager failure.</summary>
    public Exception? SaveThrows { get; set; }

    public string? Read() => Token;

    public void Save(string token) => Token = SaveThrows is { } exception ? throw exception : token;

    public void Remove() => Token = null;
}

internal sealed class RecordingAnnouncer : IAnnouncer
{
    public List<(string Text, bool IsFailure)> Announced { get; } = [];

    public void Announce(string text, bool isFailure = false) => Announced.Add((text, isFailure));
}

internal sealed class RecordingUrlLauncher : IUrlLauncher
{
    public List<Uri> Opened { get; } = [];

    /// <summary>When set, opening throws it, like a shell launch that fails.</summary>
    public Exception? Throw { get; set; }

    public Task<bool> OpenAsync(Uri uri)
    {
        if (Throw is { } exception)
        {
            throw exception;
        }

        Opened.Add(uri);
        return Task.FromResult(true);
    }
}

internal static class AzureDevOpsTesting
{
    public static AzureDevOpsServices Services(FakeAzureDevOpsClient client, FakeTokenStore? tokens = null) => new(client, tokens ?? new FakeTokenStore());
}
