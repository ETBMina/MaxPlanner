namespace MaxPlanner.Application.PullRequests;

/// <summary>
/// Where the Azure DevOps server is and the token to call it with (ADR 0031). <see cref="ToString"/> never shows the token
/// or the address, so the connection can never reach a log by accident.
/// </summary>
public sealed record AzureDevOpsConnection(Uri CollectionUrl, string Project, string Repository, string Token)
{
    public override string ToString() => nameof(AzureDevOpsConnection);
}

/// <summary>Why a call to Azure DevOps did not give its answer.</summary>
public enum AzureDevOpsFailureKind
{
    /// <summary>404: the project, repository or item does not exist.</summary>
    NotFound,

    /// <summary>409 with <c>typeKey</c> <c>GitPullRequestExistsException</c> (TF401179).</summary>
    AlreadyExists,

    /// <summary>401 or 403: the token was rejected or lacks Code (Read and write).</summary>
    Unauthorized,

    /// <summary>No answer within the request timeout (30 s).</summary>
    Timeout,

    /// <summary>The server could not be reached (name, connection, TLS, proxy).</summary>
    Network,

    /// <summary>Any other error status, or an answer the app cannot read.</summary>
    Server,
}

/// <summary>The part of a network failure the user can act on (design §5.11 "{reason}").</summary>
public enum AzureDevOpsNetworkReason
{
    Unknown,
    NameResolution,
    Connection,
    SecureConnection,
    Proxy,
}

/// <param name="ServerMessage">The server's own <c>message</c>, shown to the user; null when the answer had none.</param>
/// <param name="StatusCode">The HTTP status, when there was an answer.</param>
public sealed record AzureDevOpsFailure(
    AzureDevOpsFailureKind Kind,
    int? StatusCode = null,
    string? ServerMessage = null,
    AzureDevOpsNetworkReason NetworkReason = AzureDevOpsNetworkReason.Unknown);

/// <summary>An Azure DevOps answer: a value, or the failure that prevented it.</summary>
public readonly record struct AzureDevOpsResult<T>(T? Value, AzureDevOpsFailure? Failure)
{
    public bool IsSuccess => Failure is null;
}

/// <summary>Creates <see cref="AzureDevOpsResult{T}"/> values.</summary>
public static class AzureDevOpsResult
{
    public static AzureDevOpsResult<T> Success<T>(T value) => new(value, null);

    public static AzureDevOpsResult<T> Failed<T>(AzureDevOpsFailure failure) => new(default, failure ?? throw new ArgumentNullException(nameof(failure)));
}

/// <summary>The signed-in user (<c>_apis/connectionData</c> <c>authenticatedUser</c>).</summary>
public sealed record AzureDevOpsUser(string Id, string DisplayName);

/// <summary>
/// The Azure DevOps Server REST calls the app makes (ADR 0031, api-version 6.0). Branch names are short names or
/// <c>refs/heads/…</c>. Implementations never retry, never log the token, addresses, or branch names, and report
/// failures as <see cref="AzureDevOpsFailure"/> instead of throwing (cancellation by the caller still throws).
/// </summary>
public interface IAzureDevOpsClient
{
    /// <summary><c>GET {collection}/_apis/connectionData</c>: checks the server and the token.</summary>
    Task<AzureDevOpsResult<AzureDevOpsUser>> GetUserAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken);

    /// <summary>The repository exists in the project; returns its name as the server spells it.</summary>
    Task<AzureDevOpsResult<string>> GetRepositoryAsync(AzureDevOpsConnection connection, CancellationToken cancellationToken);

    Task<AzureDevOpsResult<bool>> BranchExistsAsync(AzureDevOpsConnection connection, string branch, CancellationToken cancellationToken);

    /// <summary>How many commits <paramref name="source"/> has that <paramref name="target"/> lacks (<c>aheadCount</c>).</summary>
    Task<AzureDevOpsResult<int>> GetCommitsAheadAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken);

    /// <summary>Creates the pull request with no description; returns its ID. Never retried.</summary>
    Task<AzureDevOpsResult<int>> CreatePullRequestAsync(AzureDevOpsConnection connection, string source, string target, string title, CancellationToken cancellationToken);

    /// <summary>Sets auto-complete by <paramref name="userId"/> and keeps the source branch.</summary>
    Task<AzureDevOpsResult<bool>> SetAutoCompleteAsync(AzureDevOpsConnection connection, int pullRequestId, string userId, CancellationToken cancellationToken);

    /// <summary>The active pull request from <paramref name="source"/> to <paramref name="target"/>, or null when there is none.</summary>
    Task<AzureDevOpsResult<int?>> FindActivePullRequestAsync(AzureDevOpsConnection connection, string source, string target, CancellationToken cancellationToken);
}

/// <summary>
/// The personal access token in Windows Credential Manager (ADR 0031): one entry for the app, readable only by the
/// current Windows user. Never written under <c>data/</c>, logged, exported or backed up.
/// </summary>
public interface IAzureDevOpsTokenStore
{
    /// <returns>The saved token, or null when none is saved.</returns>
    string? Read();

    /// <summary>Saves the token, replacing any saved one.</summary>
    void Save(string token);

    /// <summary>Removes the saved token; nothing happens when none is saved.</summary>
    void Remove();
}
