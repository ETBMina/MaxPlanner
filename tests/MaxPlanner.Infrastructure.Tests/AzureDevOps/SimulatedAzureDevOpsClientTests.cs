#if DEBUG
using AwesomeAssertions;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Infrastructure.AzureDevOps;

namespace MaxPlanner.Infrastructure.Tests.AzureDevOps;

/// <summary>ADR 0031: the Debug-only simulator's cases, through the real create use case where the outcome matters.</summary>
[Trait("Req", "B-18")]
public sealed class SimulatedAzureDevOpsClientTests
{
    private static readonly AzureDevOpsConnection Connection = new(new Uri("https://simulator/coll"), "proj", "repo", "token");

    private readonly SimulatedAzureDevOpsClient _client = new(TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public async Task Other_branches_are_created_with_ids_from_9001_and_auto_complete()
    {
        var first = await CreateAsync("feature/a");
        var second = await CreateAsync("feature/b");

        first.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.Created, 9001));
        second.PullRequestId.Should().Be(9002);
    }

    [Fact]
    public async Task Creating_the_same_branches_twice_already_exists_and_is_found()
    {
        await CreateAsync("feature/a");

        (await CreateAsync("FEATURE/A", "refs/heads/main")).Kind.Should().Be(PullRequestOutcomeKind.AlreadyExists);
        (await _client.FindActivePullRequestAsync(Connection, "feature/a", "main", CancellationToken.None)).Value.Should().Be(9001);
    }

    [Fact]
    public async Task Dup_source_already_exists_with_a_findable_pull_request()
    {
        var created = await _client.CreatePullRequestAsync(Connection, "Dup-1", "main", "T", CancellationToken.None);

        created.Failure.Should().BeEquivalentTo(new { Kind = AzureDevOpsFailureKind.AlreadyExists, StatusCode = 409 });
        (await CreateAsync("dup-1")).Kind.Should().Be(PullRequestOutcomeKind.AlreadyExists);
        (await _client.FindActivePullRequestAsync(Connection, "dup-1", "main", CancellationToken.None)).Value.Should().Be(SimulatedAzureDevOpsClient.FirstPullRequestId);
    }

    [Fact]
    public async Task Dup_source_is_found_before_any_create()
    {
        (await _client.FindActivePullRequestAsync(Connection, "dup-2", "main", CancellationToken.None)).Value.Should().Be(9001);
    }

    [Fact]
    public async Task Empty_source_has_no_new_commits()
    {
        (await CreateAsync("empty-x")).Kind.Should().Be(PullRequestOutcomeKind.NoNewCommits);
    }

    [Theory]
    [InlineData("gone-x", "main", "gone-x")]
    [InlineData("feature/a", "gone-main", "gone-main")]
    public async Task Gone_branches_are_not_found(string source, string target, string missing)
    {
        var outcome = await CreateAsync(source, target);

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.BranchNotFound, Branch: missing));
    }

    [Fact]
    public async Task Slow_source_times_out_on_create()
    {
        var created = await _client.CreatePullRequestAsync(Connection, "slow-x", "main", "T", CancellationToken.None);
        var outcome = await CreateAsync("SLOW-y");

        created.Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Timeout);
        outcome.Kind.Should().Be(PullRequestOutcomeKind.CreateOutcomeUnknown);
    }

    [Fact]
    public async Task Lost_source_has_an_unknown_outcome_but_the_pull_request_is_recorded()
    {
        var outcome = await CreateAsync("lost-x");

        outcome.Kind.Should().Be(PullRequestOutcomeKind.CreateOutcomeUnknown);
        outcome.Failure.Should().Be(new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown));
        (await _client.FindActivePullRequestAsync(Connection, "lost-x", "main", CancellationToken.None)).Value.Should().Be(9001);
        (await CreateAsync("lost-x", checkExistingFirst: true)).Kind.Should().Be(PullRequestOutcomeKind.AlreadyExists);
    }

    [Fact]
    public async Task Fail_source_gets_a_server_error()
    {
        var outcome = await CreateAsync("fail-x");

        outcome.Kind.Should().Be(PullRequestOutcomeKind.ServerError);
        outcome.Failure!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Bad_token_is_refused_on_every_call()
    {
        var bad = Connection with { Token = "bad" };

        (await _client.GetUserAsync(bad, CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.GetRepositoryAsync(bad, CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.BranchExistsAsync(bad, "a", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.GetCommitsAheadAsync(bad, "a", "b", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.CreatePullRequestAsync(bad, "a", "b", "T", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.SetAutoCompleteAsync(bad, 1, "u", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await _client.FindActivePullRequestAsync(bad, "a", "b", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.Unauthorized);
        (await CreateAsync("feature/a", connection: bad)).Kind.Should().Be(PullRequestOutcomeKind.TokenRejected);
    }

    [Fact]
    public async Task Nope_project_is_not_found_but_the_user_is()
    {
        var nope = Connection with { Project = "Nope" };

        (await _client.GetUserAsync(nope, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _client.GetRepositoryAsync(nope, CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.NotFound);
        (await _client.BranchExistsAsync(nope, "a", CancellationToken.None)).Failure!.Kind.Should().Be(AzureDevOpsFailureKind.NotFound);
    }

    [Fact]
    public async Task Repository_and_user_answer_like_the_server()
    {
        (await _client.GetRepositoryAsync(Connection, CancellationToken.None)).Value.Should().Be("repo");
        (await _client.GetUserAsync(Connection, CancellationToken.None)).Value!.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        var slowClient = new SimulatedAzureDevOpsClient(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var call = () => slowClient.GetUserAsync(Connection, cancel.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Routing_sends_only_the_simulator_host_to_the_simulator()
    {
        var real = new SimulatedAzureDevOpsClient(TimeSpan.Zero, TimeSpan.Zero);
        var routing = new AzureDevOpsRoutingClient(real, _client);
        var server = Connection with { CollectionUrl = new Uri("https://ado.example.test/coll") };

        await routing.CreatePullRequestAsync(Connection, "a", "main", "T", CancellationToken.None);
        await routing.CreatePullRequestAsync(server, "b", "main", "T", CancellationToken.None);

        (await _client.FindActivePullRequestAsync(Connection, "a", "main", CancellationToken.None)).Value.Should().NotBeNull();
        (await _client.FindActivePullRequestAsync(Connection, "b", "main", CancellationToken.None)).Value.Should().BeNull();
        (await real.FindActivePullRequestAsync(server, "b", "main", CancellationToken.None)).Value.Should().NotBeNull();
    }

    private Task<PullRequestOutcome> CreateAsync(string source, string target = "main", AzureDevOpsConnection? connection = null, bool checkExistingFirst = false) =>
        new CreatePullRequest(_client).ExecuteAsync(
            connection ?? Connection,
            new PullRequestRequest(source, target, "Title", AutoComplete: true, CheckExistingFirst: checkExistingFirst),
            _ => { },
            CancellationToken.None);
}
#endif
