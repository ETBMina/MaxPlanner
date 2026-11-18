namespace MaxPlanner.Application.PullRequests;

/// <summary>The steps of design §5.11 Progress, in order.</summary>
public enum PullRequestStep
{
    CheckingBranches,
    CheckingCommits,
    Creating,
    SettingAutoComplete,
}

/// <param name="Source">The source branch (short name or <c>refs/heads/…</c>).</param>
/// <param name="Target">The target branch.</param>
/// <param name="AutoComplete">Set auto-complete after the pull request is created.</param>
/// <param name="CheckExistingFirst">
/// An earlier create may have succeeded unseen (<see cref="PullRequestOutcomeKind.CreateOutcomeUnknown"/>): look for an
/// active pull request from the source to the target first, and report it as already existing instead of posting.
/// </param>
public sealed record PullRequestRequest(string Source, string Target, string Title, bool AutoComplete, bool CheckExistingFirst = false);

/// <summary>The results of design §5.11.</summary>
public enum PullRequestOutcomeKind
{
    Created,

    /// <summary>The pull request exists, but setting auto-complete failed.</summary>
    CreatedAutoCompleteFailed,

    /// <summary><c>aheadCount</c> 0: nothing is created, with no override.</summary>
    NoNewCommits,

    BranchNotFound,
    AlreadyExists,
    TokenRejected,

    /// <summary>Network, timeout or TLS failure.</summary>
    Unreachable,

    ServerError,

    /// <summary>
    /// The create request timed out, lost its connection, or got a success answer the app cannot read: the pull request
    /// may exist. Try again first looks for it (<see cref="PullRequestRequest.CheckExistingFirst"/>).
    /// </summary>
    CreateOutcomeUnknown,
}

/// <param name="PullRequestId">Set for <see cref="PullRequestOutcomeKind.Created"/> and <see cref="PullRequestOutcomeKind.CreatedAutoCompleteFailed"/>.</param>
/// <param name="Branch">The missing branch for <see cref="PullRequestOutcomeKind.BranchNotFound"/>.</param>
/// <param name="Failure">The failure behind every outcome that is not a success, when there was one.</param>
public sealed record PullRequestOutcome(PullRequestOutcomeKind Kind, int? PullRequestId = null, string? Branch = null, AzureDevOpsFailure? Failure = null)
{
    /// <summary>A pull request exists now; Try again must not be offered (design §5.11).</summary>
    public bool IsCreated => Kind is PullRequestOutcomeKind.Created or PullRequestOutcomeKind.CreatedAutoCompleteFailed;
}

/// <summary>
/// Creates one pull request (ADR 0031 Flow, design §5.11): the user (needed for auto-complete, and an early token check),
/// both branches, the commits-ahead check, the create, and auto-complete when asked. Nothing is retried, and nothing is
/// created when the source has no new commits. Not a dataset write, so it does not use the command runner (ADR 0029).
/// </summary>
/// <remarks>
/// Awaits continue on the caller's context, so <c>onStep</c> runs on the UI thread when the caller is there.
/// </remarks>
public sealed class CreatePullRequest(IAzureDevOpsClient client)
{
    private readonly IAzureDevOpsClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<PullRequestOutcome> ExecuteAsync(
        AzureDevOpsConnection connection,
        PullRequestRequest request,
        Action<PullRequestStep> onStep,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onStep);
        var source = BranchNames.ToShortName(request.Source);
        var target = BranchNames.ToShortName(request.Target);

        onStep(PullRequestStep.CheckingBranches);
        var user = await _client.GetUserAsync(connection, cancellationToken).ConfigureAwait(true);
        if (user.Failure is { } userFailure)
        {
            return Failed(userFailure);
        }

        foreach (var branch in new[] { source, target })
        {
            var exists = await _client.BranchExistsAsync(connection, branch, cancellationToken).ConfigureAwait(true);
            if (exists.Failure is { } branchFailure)
            {
                return Failed(branchFailure);
            }

            if (!exists.Value)
            {
                return new PullRequestOutcome(PullRequestOutcomeKind.BranchNotFound, Branch: branch);
            }
        }

        onStep(PullRequestStep.CheckingCommits);
        var ahead = await _client.GetCommitsAheadAsync(connection, source, target, cancellationToken).ConfigureAwait(true);
        if (ahead.Failure is { } aheadFailure)
        {
            return Failed(aheadFailure);
        }

        if (ahead.Value <= 0)
        {
            return new PullRequestOutcome(PullRequestOutcomeKind.NoNewCommits);
        }

        onStep(PullRequestStep.Creating);
        if (request.CheckExistingFirst)
        {
            var existing = await _client.FindActivePullRequestAsync(connection, source, target, cancellationToken).ConfigureAwait(true);
            if (existing.Failure is { } findFailure)
            {
                return Failed(findFailure);
            }

            if (existing.Value is not null)
            {
                return new PullRequestOutcome(PullRequestOutcomeKind.AlreadyExists);
            }
        }

        var created = await _client.CreatePullRequestAsync(connection, source, target, request.Title.Trim(), cancellationToken).ConfigureAwait(true);
        if (created.Failure is { } createFailure)
        {
            return MayHaveCreated(createFailure)
                ? new PullRequestOutcome(PullRequestOutcomeKind.CreateOutcomeUnknown, Failure: createFailure)
                : Failed(createFailure);
        }

        var id = created.Value;
        if (!request.AutoComplete)
        {
            return new PullRequestOutcome(PullRequestOutcomeKind.Created, id);
        }

        onStep(PullRequestStep.SettingAutoComplete);
        var autoComplete = await _client.SetAutoCompleteAsync(connection, id, user.Value!.Id, cancellationToken).ConfigureAwait(true);
        return autoComplete.Failure is { } autoCompleteFailure
            ? new PullRequestOutcome(PullRequestOutcomeKind.CreatedAutoCompleteFailed, id, Failure: autoCompleteFailure)
            : new PullRequestOutcome(PullRequestOutcomeKind.Created, id);
    }

    // The request may have reached the server: no answer in time, a connection lost after it was set up, or a success
    // status with an answer the app cannot read. A name, TLS, proxy or connection-setup failure means nothing was sent,
    // and an error status means the server refused it.
    private static bool MayHaveCreated(AzureDevOpsFailure failure) => failure switch
    {
        { Kind: AzureDevOpsFailureKind.Timeout } => true,
        { Kind: AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown } => true,
        { Kind: AzureDevOpsFailureKind.Server, StatusCode: >= 200 and < 300 } => true,
        _ => false,
    };

    private static PullRequestOutcome Failed(AzureDevOpsFailure failure) => new(
        failure.Kind switch
        {
            AzureDevOpsFailureKind.AlreadyExists => PullRequestOutcomeKind.AlreadyExists,
            AzureDevOpsFailureKind.Unauthorized => PullRequestOutcomeKind.TokenRejected,
            AzureDevOpsFailureKind.Timeout or AzureDevOpsFailureKind.Network => PullRequestOutcomeKind.Unreachable,
            _ => PullRequestOutcomeKind.ServerError,
        },
        Failure: failure);
}
