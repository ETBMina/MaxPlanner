using AwesomeAssertions;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Application.Tests.Fakes;

namespace MaxPlanner.Application.Tests.PullRequests;

/// <summary>ADR 0031 Flow and design §5.11: the steps, the no-new-commits refusal, and the result for each answer.</summary>
[Trait("Req", "B-18")]
public sealed class CreatePullRequestTests
{
    private static readonly AzureDevOpsConnection Connection = new(new Uri("https://server/value"), "ace", "repo", "pat");

    private readonly FakeAzureDevOpsClient _client = new();
    private readonly List<PullRequestStep> _steps = [];

    [Fact]
    public async Task A_pull_request_is_created_after_every_check_without_auto_complete()
    {
        var outcome = await RunAsync(new PullRequestRequest("refs/heads/12345", "uat", "  PR 12345 into uat ", AutoComplete: false));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.Created, 42));
        outcome.IsCreated.Should().BeTrue();
        _client.Created.Should().Be(("12345", "uat", "PR 12345 into uat"));
        _client.Calls.Should().Equal("GetUserAsync", "BranchExistsAsync", "BranchExistsAsync", "GetCommitsAheadAsync", "CreatePullRequestAsync");
        _steps.Should().Equal(PullRequestStep.CheckingBranches, PullRequestStep.CheckingCommits, PullRequestStep.Creating);
    }

    [Fact]
    public async Task Auto_complete_is_set_by_the_signed_in_user()
    {
        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true));

        outcome.Kind.Should().Be(PullRequestOutcomeKind.Created);
        _client.AutoCompleteUserId.Should().Be("user-1");
        _steps.Should().EndWith(PullRequestStep.SettingAutoComplete);
    }

    [Fact]
    public async Task A_failed_auto_complete_still_reports_the_created_pull_request()
    {
        var failure = new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 400, "Policy evaluation failed");
        _client.Failures["SetAutoCompleteAsync"] = failure;

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.CreatedAutoCompleteFailed, 42, Failure: failure));
        outcome.IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task No_new_commits_creates_nothing()
    {
        _client.CommitsAhead = 0;

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true));

        outcome.Kind.Should().Be(PullRequestOutcomeKind.NoNewCommits);
        _client.Calls.Should().NotContain("CreatePullRequestAsync");
    }

    [Theory]
    [InlineData("missing", "uat", "missing")]
    [InlineData("12345", "release", "release")]
    public async Task A_missing_branch_is_named_and_nothing_is_created(string source, string target, string missing)
    {
        var outcome = await RunAsync(new PullRequestRequest(source, target, "T", AutoComplete: false));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.BranchNotFound, Branch: missing));
        _client.Calls.Should().NotContain("GetCommitsAheadAsync").And.NotContain("CreatePullRequestAsync");
    }

    [Theory]
    [InlineData(AzureDevOpsFailureKind.Unauthorized, PullRequestOutcomeKind.TokenRejected)]
    [InlineData(AzureDevOpsFailureKind.Timeout, PullRequestOutcomeKind.Unreachable)]
    [InlineData(AzureDevOpsFailureKind.Network, PullRequestOutcomeKind.Unreachable)]
    [InlineData(AzureDevOpsFailureKind.Server, PullRequestOutcomeKind.ServerError)]
    [InlineData(AzureDevOpsFailureKind.NotFound, PullRequestOutcomeKind.ServerError)]
    public async Task A_failed_first_call_stops_the_flow(AzureDevOpsFailureKind kind, PullRequestOutcomeKind expected)
    {
        _client.Failures["GetUserAsync"] = new AzureDevOpsFailure(kind);

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: false));

        outcome.Kind.Should().Be(expected);
        outcome.IsCreated.Should().BeFalse();
        _client.Calls.Should().Equal("GetUserAsync");
    }

    [Fact]
    public async Task An_existing_pull_request_is_reported_and_the_create_is_not_retried()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.AlreadyExists, 409, "TF401179: An active pull request for the source and target branch already exists.");

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true));

        outcome.Kind.Should().Be(PullRequestOutcomeKind.AlreadyExists);
        _client.Calls.Count(c => c == "CreatePullRequestAsync").Should().Be(1);
        _client.Calls.Should().NotContain("SetAutoCompleteAsync");
    }

    public static TheoryData<AzureDevOpsFailure> UnclearCreateFailures() =>
    [
        new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout),
        new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.Unknown),
        new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 201),
    ];

    [Theory]
    [MemberData(nameof(UnclearCreateFailures))]
    public async Task A_create_that_may_have_reached_the_server_is_an_unknown_outcome(AzureDevOpsFailure failure)
    {
        _client.Failures["CreatePullRequestAsync"] = failure;

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.CreateOutcomeUnknown, Failure: failure));
        outcome.IsCreated.Should().BeFalse();
        _client.Calls.Should().NotContain("SetAutoCompleteAsync");
    }

    [Theory]
    [InlineData(AzureDevOpsNetworkReason.NameResolution)]
    [InlineData(AzureDevOpsNetworkReason.Connection)]
    [InlineData(AzureDevOpsNetworkReason.SecureConnection)]
    [InlineData(AzureDevOpsNetworkReason.Proxy)]
    public async Task A_create_that_was_never_sent_is_unreachable(AzureDevOpsNetworkReason reason)
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: reason);

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: false));

        outcome.Kind.Should().Be(PullRequestOutcomeKind.Unreachable);
    }

    [Fact]
    public async Task An_error_status_on_the_create_is_a_server_error()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 500);

        (await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: false))).Kind.Should().Be(PullRequestOutcomeKind.ServerError);
    }

    [Fact]
    public async Task Checking_first_reports_a_pull_request_the_unclear_create_made_and_posts_nothing()
    {
        _client.ActivePullRequest = 1190;

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: true, CheckExistingFirst: true));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.AlreadyExists));
        _client.Calls.Should().Contain("FindActivePullRequestAsync").And.NotContain("CreatePullRequestAsync");
    }

    [Fact]
    public async Task Checking_first_creates_when_no_pull_request_is_found()
    {
        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: false, CheckExistingFirst: true));

        outcome.Should().Be(new PullRequestOutcome(PullRequestOutcomeKind.Created, 42));
        _client.Calls.Should().EndWith(["FindActivePullRequestAsync", "CreatePullRequestAsync"]);
    }

    [Fact]
    public async Task A_failed_check_first_posts_nothing()
    {
        _client.Failures["FindActivePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout);

        var outcome = await RunAsync(new PullRequestRequest("12345", "uat", "T", AutoComplete: false, CheckExistingFirst: true));

        outcome.Kind.Should().Be(PullRequestOutcomeKind.Unreachable);
        _client.Calls.Should().NotContain("CreatePullRequestAsync");
    }

    private Task<PullRequestOutcome> RunAsync(PullRequestRequest request) =>
        new CreatePullRequest(_client).ExecuteAsync(Connection, request, _steps.Add, TestContext.Current.CancellationToken);
}
