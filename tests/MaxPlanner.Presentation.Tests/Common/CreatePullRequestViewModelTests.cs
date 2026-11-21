using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common.PullRequests;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>Design §5.11: the Create pull request dialog's defaults, validation, progress and every result row.</summary>
[Trait("Req", "B-18")]
public sealed class CreatePullRequestViewModelTests
{
    private static readonly AzureDevOpsPreferences Settings = new()
    {
        Enabled = true,
        CollectionUrl = "https://server/value",
        Project = "ace_bank",
        Repository = "ace_repo",
    };

    private static readonly PullRequestSource Source = new("Card freeze", "CR-1042", "12345", "Card freeze");

    private readonly FakeAzureDevOpsClient _client = new();
    private readonly FakeTokenStore _tokens = new();
    private readonly RecordingAnnouncer _announcer = new();
    private readonly RecordingUrlLauncher _urls = new();
    private readonly FakeStrings _strings = new FakeStrings()
        .With("PullRequest_Caption", "{0} · {1}")
        .With("PullRequest_Created", "Pull request {0} created.")
        .With("PullRequest_CreatedAutoCompleteFailed", "Pull request {0} created, but auto-complete couldn't be set: {1}.")
        .With("PullRequest_NoNewCommits", "{0} has no new commits compared with {1}. Push your work first.")
        .With("PullRequest_BranchNotFound", "Branch {0} doesn't exist in {1}.")
        .With("PullRequest_AlreadyExists", "A pull request from {0} to {1} already exists.")
        .With("PullRequest_Unreachable", "Couldn't reach {0}: {1}.")
        .With("PullRequest_ServerError", "Azure DevOps returned an error: {0}.")
        .With("PullRequest_SetupIncomplete", "Azure DevOps isn't set up yet. Missing: {0}.")
        .With("PullRequest_LinkName", "Open pull request {0} in browser")
        .With("AzureDevOps_MissingToken", "personal access token")
        .With("AzureDevOps_ReasonSecureConnection", "the secure connection (TLS) failed");

    [Fact]
    public void The_dialog_starts_from_the_saved_record_and_the_Settings_defaults()
    {
        var dialog = Create(Settings with { DefaultTarget = "release", AutoComplete = false });

        dialog.SourceBranch.Should().Be("12345");
        dialog.TargetBranch.Should().Be("release");
        dialog.Title.Should().Be("PR CR-1042 12345 into release");
        dialog.IsAutoComplete.Should().BeFalse();
        dialog.Caption.Should().Be("ace_bank · ace_repo");
        dialog.HasNoTaskNumber.Should().BeFalse();
        dialog.CanCreate.Should().BeTrue();
        dialog.HasResult.Should().BeFalse();
        dialog.SourceError.Should().BeNull("an untouched valid field shows no error");
    }

    [Fact]
    public void Without_a_task_number_the_source_starts_empty_with_a_helper()
    {
        var dialog = Create(source: Source with { TaskNumber = null });

        dialog.SourceBranch.Should().BeEmpty();
        dialog.HasNoTaskNumber.Should().BeTrue();
        dialog.SourceError.Should().BeNull("an empty field is not an error until it is edited");
        dialog.CanCreate.Should().BeFalse();
    }

    [Fact]
    public void The_title_follows_the_branches_until_the_user_edits_it()
    {
        var dialog = Create();

        dialog.TargetBranch = "main";
        dialog.Title.Should().Be("PR CR-1042 12345 into main");

        dialog.Title = "My own title";
        dialog.TargetBranch = "uat";
        dialog.Title.Should().Be("My own title");
    }

    [Fact]
    public void Invalid_branches_and_an_empty_title_disable_Create()
    {
        var dialog = Create();

        dialog.SourceBranch = "my branch";
        dialog.SourceError.Should().Be("PullRequest_BranchInvalidError");
        dialog.CanCreate.Should().BeFalse();

        dialog.SourceBranch = "refs/heads/UAT";
        dialog.SourceError.Should().BeNull();
        dialog.TargetError.Should().Be("PullRequest_SameBranchError");
        dialog.CanCreate.Should().BeFalse();

        dialog.SourceBranch = "12345";
        dialog.Title = " ";
        dialog.TitleError.Should().Be("PullRequest_TitleRequiredError");
        dialog.IsPrimaryEnabled.Should().BeFalse();

        dialog.SourceBranch = string.Empty;
        dialog.SourceError.Should().Be("PullRequest_BranchRequiredError");
    }

    [Fact]
    public void A_missing_token_is_incomplete_setup_with_Open_Settings()
    {
        _tokens.Remove();
        var dialog = Create();
        var closed = 0;
        dialog.CloseRequested += (_, _) => closed++;

        dialog.IsSetupIncomplete.Should().BeTrue();
        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Warning);
        dialog.ResultMessage.Should().Be("Azure DevOps isn't set up yet. Missing: personal access token.");
        dialog.ShowsOpenSettings.Should().BeTrue();
        dialog.CanCreate.Should().BeFalse();
        dialog.CanEditFields.Should().BeFalse();

        dialog.OpenSettingsCommand.Execute(null);

        dialog.IsOpenSettingsRequested.Should().BeTrue();
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Created_shows_the_link_and_Create_becomes_Close()
    {
        var dialog = Create();
        var focus = new List<string>();
        dialog.FocusRequested += (_, field) => focus.Add(field);

        await dialog.CreateAsync();

        dialog.IsCreated.Should().BeTrue();
        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Success);
        dialog.ResultMessage.Should().Be("Pull request #1187 created.");
        (dialog.ResultBeforeLink, dialog.LinkText, dialog.ResultAfterLink).Should().Be(("Pull request ", "#1187", " created."));
        dialog.LinkName.Should().Be("Open pull request #1187 in browser");
        dialog.CreatedUrl!.AbsoluteUri.Should().Be("https://server/value/ace_bank/_git/ace_repo/pullrequest/1187");
        dialog.PrimaryButtonText.Should().Be("PullRequest_CloseButton");
        dialog.CloseButtonText.Should().BeEmpty();
        dialog.ShowsTryAgain.Should().BeFalse();
        dialog.CanEditFields.Should().BeFalse();
        focus.Should().Equal(CreatePullRequestViewModel.ResultFocusKey);
        _client.Calls.Should().Contain("SetAutoCompleteAsync", "auto-complete defaults to on");
        _announcer.Announced.Select(a => a.Text).Should().Contain(["PullRequest_StepCheckingBranches", "PullRequest_StepCreating", "Pull request #1187 created."]);

        await dialog.OpenPullRequestCommand.ExecuteAsync(null);
        _urls.Opened.Should().Equal(dialog.CreatedUrl);

        var closed = 0;
        dialog.CloseRequested += (_, _) => closed++;
        await dialog.SaveCommand.ExecuteAsync(null);
        closed.Should().Be(1);
        _client.Calls.Count(c => c == "CreatePullRequestAsync").Should().Be(1, "Close never creates a second pull request");
    }

    [Fact]
    public async Task A_failed_auto_complete_is_a_warning_with_the_server_message()
    {
        _client.Failures["SetAutoCompleteAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 400, "Policy evaluation failed.");
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Warning);
        dialog.ResultMessage.Should().Be("Pull request #1187 created, but auto-complete couldn't be set: Policy evaluation failed.");
        dialog.LinkText.Should().Be("#1187");
        dialog.ShowsTryAgain.Should().BeFalse("a pull request exists now");
    }

    [Fact]
    public async Task No_new_commits_unlocks_the_fields_and_offers_no_override()
    {
        _client.CommitsAhead = 0;
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Warning);
        dialog.ResultMessage.Should().Be("12345 has no new commits compared with uat. Push your work first.");
        dialog.CanEditFields.Should().BeTrue();
        (dialog.ShowsTryAgain, dialog.ShowsOpenExisting, dialog.ShowsOpenSettings).Should().Be((false, false, false));
        dialog.HasResultActions.Should().BeFalse("an empty button row would pad the bar");
        _client.Calls.Should().NotContain("CreatePullRequestAsync");
    }

    [Fact]
    public async Task A_missing_branch_names_the_branch_and_the_repository()
    {
        var dialog = Create();
        dialog.TargetBranch = "release";

        await dialog.CreateAsync();

        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Error);
        dialog.ResultMessage.Should().Be("Branch release doesn't exist in ace_repo.");
        dialog.CanEditFields.Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_pull_request_offers_Open_existing_PR()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.AlreadyExists, 409, "TF401179");
        _client.ActivePullRequest = 1180;
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Informational);
        dialog.ResultMessage.Should().Be("A pull request from 12345 to uat already exists.");
        dialog.ShowsOpenExisting.Should().BeTrue();
        dialog.HasResultActions.Should().BeTrue();
        dialog.ShowsTryAgain.Should().BeFalse();

        await dialog.OpenExistingCommand.ExecuteAsync(null);

        _urls.Opened.Single().AbsoluteUri.Should().Be("https://server/value/ace_bank/_git/ace_repo/pullrequest/1180");
    }

    [Fact]
    public async Task A_rejected_token_offers_Open_Settings()
    {
        _client.Failures["GetUserAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Unauthorized, 401);
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultMessage.Should().Be("PullRequest_TokenRejected");
        dialog.ShowsOpenSettings.Should().BeTrue();
        dialog.ShowsTryAgain.Should().BeFalse();
    }

    [Fact]
    public async Task A_network_failure_offers_Try_again_and_nothing_is_retried_by_itself()
    {
        _client.Failures["GetUserAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Network, NetworkReason: AzureDevOpsNetworkReason.SecureConnection);
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultMessage.Should().Be("Couldn't reach server: the secure connection (TLS) failed.");
        dialog.ShowsTryAgain.Should().BeTrue();
        _client.Calls.Should().Equal("GetUserAsync");

        _client.Failures.Clear();
        await dialog.TryAgainCommand.ExecuteAsync(null);

        dialog.IsCreated.Should().BeTrue();
        _client.Calls.Count(c => c == "CreatePullRequestAsync").Should().Be(1);
    }

    [Fact]
    public async Task A_server_error_shows_the_server_message_and_Try_again()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 500, "TF400898: An internal error occurred.");
        var dialog = Create();

        await dialog.CreateAsync();

        dialog.ResultMessage.Should().Be("Azure DevOps returned an error: TF400898: An internal error occurred.");
        dialog.ShowsTryAgain.Should().BeTrue();
    }

    [Fact]
    public async Task An_unclear_create_offers_Open_existing_PR_and_Try_again_looks_before_posting()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Timeout);
        var dialog = Create();
        dialog.IsAutoComplete = false;

        await dialog.CreateAsync();

        dialog.ResultSeverity.Should().Be(PullRequestResultSeverity.Warning);
        dialog.ResultMessage.Should().Be("PullRequest_OutcomeUnknown");
        dialog.ShowsOpenExisting.Should().BeTrue();
        dialog.ShowsTryAgain.Should().BeTrue();
        dialog.IsCreated.Should().BeFalse();

        _client.Failures.Clear();
        _client.ActivePullRequest = 1190;
        await dialog.TryAgainCommand.ExecuteAsync(null);

        dialog.ResultMessage.Should().Be("A pull request from 12345 to uat already exists.");
        dialog.ShowsOpenExisting.Should().BeTrue();
        _client.Calls.Count(c => c == "CreatePullRequestAsync").Should().Be(1, "the pull request was found, so nothing is posted again");
    }

    [Fact]
    public async Task After_an_unclear_create_Try_again_creates_when_nothing_is_found()
    {
        _client.Failures["CreatePullRequestAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Server, 201);
        var dialog = Create();
        dialog.IsAutoComplete = false;
        await dialog.CreateAsync();
        _client.Failures.Clear();
        _client.Calls.Clear();

        await dialog.TryAgainCommand.ExecuteAsync(null);

        dialog.IsCreated.Should().BeTrue();
        _client.Calls.Should().EndWith(["FindActivePullRequestAsync", "CreatePullRequestAsync"]);
    }

    [Fact]
    public async Task A_failing_link_launch_is_reported_not_thrown()
    {
        var dialog = Create();
        dialog.IsAutoComplete = false;
        await dialog.CreateAsync();
        _urls.Throw = new InvalidOperationException("launch failed");

        var act = () => dialog.OpenPullRequestCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task While_a_request_runs_the_fields_lock_and_the_dialog_does_not_close()
    {
        _client.Gate = new TaskCompletionSource();
        var dialog = Create();
        dialog.IsAutoComplete = false;

        var running = dialog.CreateAsync();

        dialog.IsRunning.Should().BeTrue();
        dialog.IsSaving.Should().BeTrue();
        dialog.CanEditFields.Should().BeFalse();
        dialog.IsPrimaryEnabled.Should().BeFalse();
        dialog.TryClose().Should().BeFalse();
        dialog.Steps.Select(s => s.Step).Should().Equal(PullRequestStep.CheckingBranches, PullRequestStep.CheckingCommits, PullRequestStep.Creating);
        dialog.Steps[0].IsActive.Should().BeTrue();

        _client.Gate.SetResult();
        await running;

        dialog.IsRunning.Should().BeFalse();
        dialog.TryClose().Should().BeTrue();
        dialog.HasSteps.Should().BeFalse();
    }

    private CreatePullRequestViewModel Create(AzureDevOpsPreferences? settings = null, PullRequestSource? source = null) =>
        new(source ?? Source, settings ?? Settings, AzureDevOpsTesting.Services(_client, _tokens), _strings, _announcer, _urls);
}
