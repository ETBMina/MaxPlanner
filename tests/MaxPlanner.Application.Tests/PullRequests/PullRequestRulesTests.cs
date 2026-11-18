using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Application.Settings;

namespace MaxPlanner.Application.Tests.PullRequests;

/// <summary>ADR 0031, design §4.6 and §5.11: the title template, branch names, the collection URL, setup, and links.</summary>
[Trait("Req", "B-18")]
public sealed class PullRequestRulesTests
{
    private static readonly PullRequestTitleValues All = new("CR-1042", "12345", "Card freeze", "12345", "uat");

    [Fact]
    public void The_default_title_names_the_CR_the_task_and_the_target() =>
        PullRequestTitleFormatter.Apply(null, All).Should().Be("PR CR-1042 12345 into uat");

    [Fact]
    public void A_blank_template_falls_back_to_the_default()
    {
        PullRequestTitleFormatter.Normalize("   ").Should().Be(PullRequestTitleFormatter.Default);
        PullRequestTitleFormatter.Apply("  ", All).Should().Be("PR CR-1042 12345 into uat");
    }

    [Fact]
    public void Every_title_placeholder_fills_case_insensitively() =>
        PullRequestTitleFormatter.Apply("{title} ({source} -> {TARGET}) {crnum}/{TaskNum}", All with { Source = "feature/x" })
            .Should().Be("Card freeze (feature/x -> uat) CR-1042/12345");

    [Fact]
    public void Placeholders_the_title_does_not_know_stay_as_typed() =>
        PullRequestTitleFormatter.Apply("{Service} {Squad} {TaskNum}", All).Should().Be("{Service} {Squad} 12345");

    // Owner decision (design §4.6): an empty value removes only its placeholder; the words around it stay.
    [Theory]
    [InlineData(null, "12345", "PR 12345 into uat")]
    [InlineData("CR-1042", null, "PR CR-1042 into uat")]
    [InlineData("CR-1042", " ", "PR CR-1042 into uat")]
    [InlineData(null, "", "PR into uat")]
    [InlineData("CR-1042", "12345", "PR CR-1042 12345 into uat")]
    public void An_empty_value_removes_only_its_placeholder(string? crNumber, string? taskNumber, string expected) =>
        PullRequestTitleFormatter.Apply(null, All with { CrNumber = crNumber, TaskNumber = taskNumber }).Should().Be(expected);

    [Fact]
    public void Whitespace_collapses_to_one_space_and_the_ends_are_trimmed() =>
        PullRequestTitleFormatter.Apply("  {CRNum}   -  {TaskNum}\t{Title}  ", All with { CrNumber = null })
            .Should().Be("- 12345 Card freeze");

    [Fact]
    public void Separators_next_to_an_empty_value_stay_unlike_Copy_ID() =>
        PullRequestTitleFormatter.Apply("[{CRNum}] {TaskNum}: {Title}", All with { TaskNumber = null })
            .Should().Be("[CR-1042] : Card freeze");

    [Fact]
    public void The_shared_placeholder_core_takes_any_lookup() =>
        CopyIdFormatter.Apply("[{A}] - {B} {C}", name => name switch
        {
            "A" => (true, "one"),
            "B" => (true, null),
            _ => (false, null),
        }).Should().Be("[one", "trailing text needs the last known placeholder's value, as in Copy ID");

    [Theory]
    [InlineData("12345", "12345")]
    [InlineData("  feature/card-freeze ", "feature/card-freeze")]
    [InlineData("refs/heads/release/2026.11", "release/2026.11")]
    public void Short_and_full_branch_names_are_accepted(string typed, string shortName)
    {
        BranchNames.Check(typed).Should().Be(BranchNameProblem.None);
        BranchNames.ToShortName(typed).Should().Be(shortName);
        BranchNames.ToRefName(typed).Should().Be("refs/heads/" + shortName);
    }

    [Theory]
    [InlineData("my branch")]
    [InlineData("a~b")]
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a\\b")]
    [InlineData("a..b")]
    [InlineData("feature/")]
    [InlineData("topic.lock")]
    [InlineData("feature/.hidden")]
    [InlineData("a//b")]
    [InlineData("a@{1}")]
    [InlineData("-x")]
    [InlineData("end.")]
    [InlineData("@")]
    public void Names_that_break_git_rules_are_invalid(string typed) =>
        BranchNames.Check(typed).Should().Be(BranchNameProblem.Invalid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("refs/heads/")]
    public void A_missing_branch_name_is_empty(string? typed) =>
        BranchNames.Check(typed).Should().Be(BranchNameProblem.Empty);

    [Fact]
    public void Branches_compare_without_case_and_prefix() =>
        BranchNames.AreSame("refs/heads/UAT", " uat ").Should().BeTrue();

    [Theory]
    [InlineData(null, CollectionUrlProblem.Empty)]
    [InlineData("  ", CollectionUrlProblem.Empty)]
    [InlineData("http://server/collection", CollectionUrlProblem.NotHttps)]
    [InlineData("server/collection", CollectionUrlProblem.NotHttps)]
    [InlineData("https://server/collection/project/_git/repo", CollectionUrlProblem.NotCollection)]
    [InlineData("https://server/collection/_apis/connectionData", CollectionUrlProblem.NotCollection)]
    [InlineData("https://server/collection/_git", CollectionUrlProblem.NotCollection)]
    [InlineData("https://user:secret@server/collection", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://user@server/collection", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://@server/collection", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://server/collection?api-version=6.0", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://server/collection?", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://server/collection#top", CollectionUrlProblem.ExtraParts)]
    [InlineData("https://server/collection", CollectionUrlProblem.None)]
    [InlineData("https://server:8443/tfs/DefaultCollection/", CollectionUrlProblem.None)]
    public void The_collection_url_must_be_an_https_collection_address(string? typed, CollectionUrlProblem expected) =>
        CollectionUrls.Check(typed).Should().Be(expected);

    [Fact]
    public void A_usable_collection_url_loses_its_trailing_slash() =>
        CollectionUrls.Parse(" https://server/tfs/value/ ")!.AbsoluteUri.Should().Be("https://server/tfs/value");

    [Fact]
    public void The_pull_request_link_escapes_the_project_and_repository() =>
        PullRequestLinks.WebUrl(new Uri("https://server/value"), "ace bank", "ace_bank", 77).AbsoluteUri
            .Should().Be("https://server/value/ace%20bank/_git/ace_bank/pullrequest/77");

    [Fact]
    public void Setup_lists_what_is_missing_in_Settings_order()
    {
        AzureDevOpsSetup.Missing(null, hasToken: false).Should().Equal(
            AzureDevOpsSetupItem.CollectionUrl, AzureDevOpsSetupItem.Project, AzureDevOpsSetupItem.Repository, AzureDevOpsSetupItem.Token);

        var settings = new AzureDevOpsPreferences { CollectionUrl = "https://server/value/_git/x", Project = "ace", Repository = "ace" };
        AzureDevOpsSetup.Missing(settings, hasToken: true).Should().Equal(AzureDevOpsSetupItem.CollectionUrl);
        AzureDevOpsSetup.Connect(settings, "pat").Should().BeNull();
    }

    [Fact]
    public void Complete_setup_gives_a_connection_that_never_prints_its_token()
    {
        var settings = new AzureDevOpsPreferences { CollectionUrl = "https://server/value/", Project = " ace ", Repository = "repo" };

        var connection = AzureDevOpsSetup.Connect(settings, "secret-pat")!;

        AzureDevOpsSetup.Missing(settings, hasToken: true).Should().BeEmpty();
        connection.CollectionUrl.AbsoluteUri.Should().Be("https://server/value");
        connection.Project.Should().Be("ace");
        connection.ToString().Should().NotContain("secret-pat").And.NotContain("server");
        AzureDevOpsSetup.Connect(settings, null).Should().BeNull("a token missing from Credential Manager counts as missing");
    }

    [Fact]
    public void Restoring_default_UI_preferences_keeps_the_Azure_DevOps_settings()
    {
        var azureDevOps = new AzureDevOpsPreferences { Enabled = true, CollectionUrl = "https://server/value", DefaultTarget = "main", AutoComplete = false };
        var current = new UiPreferences { Theme = ThemePreference.Dark, ShowBoardCardTags = false, AzureDevOps = azureDevOps };

        var restored = current.RestoreDefaults();

        restored.Should().Be(UiPreferences.Default with { AzureDevOps = azureDevOps });
        restored.AzureDevOps!.EffectiveDefaultTarget.Should().Be("main");
        AzureDevOpsPreferences.Default.EffectiveDefaultTarget.Should().Be("uat");
        AzureDevOpsPreferences.Default.EffectiveAutoComplete.Should().BeTrue();
        AzureDevOpsPreferences.Default.Enabled.Should().BeFalse("the integration is off by default");
    }
}
