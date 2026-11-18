using AwesomeAssertions;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Settings;

/// <summary>Design §4.6 Integrations: values save on change, the token goes only to the token store, and Test connection.</summary>
[Trait("Req", "B-18")]
public sealed class AzureDevOpsSettingsViewModelTests
{
    private readonly FakeAzureDevOpsClient _client = new();
    private readonly FakeTokenStore _tokens = new(null);

    [Fact]
    public void Values_save_on_change_and_blanks_mean_not_set()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);

        settings.IsEnabled = true;
        settings.CollectionUrl = " https://server/value ";
        settings.Project = "ace_bank";
        settings.DefaultTarget = "  ";
        settings.IsAutoComplete = false;

        h.Preferences.Current.AzureDevOps.Should().Be(new AzureDevOpsPreferences
        {
            Enabled = true,
            CollectionUrl = "https://server/value",
            Project = "ace_bank",
            AutoComplete = false,
        });
        settings.IsExpanded.Should().BeFalse("the switch does not open or close the expander");
        h.Workspace.Context.IsAzureDevOpsEnabled.Should().BeTrue();

        settings.IsEnabled = false;
        h.Workspace.Context.IsAzureDevOpsEnabled.Should().BeFalse();
        h.Preferences.Current.AzureDevOps!.Project.Should().Be("ace_bank", "turning it off keeps the values");
        settings.IsExpanded.Should().BeFalse("the switch does not open or close the expander");
    }

    [Fact]
    public void The_collection_url_hint_explains_an_unusable_address()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);

        settings.CollectionUrl = "http://server/value";
        settings.CollectionUrlHint.Should().Be("Settings_AdoCollectionUrlHttpsHint");
        settings.CollectionUrl = "https://server/value/ace/_git/repo";
        settings.CollectionUrlHint.Should().Be("Settings_AdoCollectionUrlCollectionHint");
        settings.CollectionUrl = "https://server/value";
        settings.CollectionUrlHint.Should().BeNull();
    }

    [Fact]
    public void A_saved_token_shows_as_Saved_and_Remove_asks_first()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);
        settings.IsTokenBoxShown.Should().BeTrue();

        settings.SaveToken("  new-pat ");
        _tokens.Token.Should().Be("new-pat");
        settings.IsSavedTokenShown.Should().BeTrue();

        settings.ChangeTokenCommand.Execute(null);
        settings.IsTokenBoxShown.Should().BeTrue();
        settings.SaveToken(string.Empty);
        settings.IsSavedTokenShown.Should().BeTrue("an empty box keeps the saved token");

        var answer = ConfirmationResult.Cancel;
        h.Workspace.Context.DialogRequested += (_, request) =>
        {
            ((ConfirmationViewModel)request.ViewModel).Choose(answer);
            request.MarkClosed();
        };
        h.Await(settings.RemoveTokenAsync());
        _tokens.Token.Should().Be("new-pat");

        answer = ConfirmationResult.Primary;
        h.Await(settings.RemoveTokenAsync());
        _tokens.Token.Should().BeNull();
        settings.HasToken.Should().BeFalse();
    }

    [Fact]
    public void Test_connection_reports_the_user_and_the_repository_or_what_failed()
    {
        _tokens.Save("pat");
        using var h = Harness(new UiPreferences
        {
            AzureDevOps = new AzureDevOpsPreferences { Enabled = true, CollectionUrl = "https://server/value", Project = "ace", Repository = "repo" },
        });
        h.Strings.With("Settings_AdoTestConnected", "Connected as {0} · {1} found").With("Settings_AdoTestNotFound", "Project {0} or repository {1} wasn't found.");
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);

        h.Await(settings.TestConnectionAsync());
        (settings.IsTestSuccess, settings.TestResult).Should().Be((true, "Connected as Max · repo found"));

        _client.Failures["GetRepositoryAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.NotFound, 404);
        h.Await(settings.TestConnectionAsync());
        (settings.IsTestSuccess, settings.TestResult).Should().Be((false, "Project ace or repository repo wasn't found."));

        _client.Failures["GetUserAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.Unauthorized, 401);
        h.Await(settings.TestConnectionAsync());
        settings.TestResult.Should().Be("PullRequest_TokenRejected");
    }

    [Fact]
    public void A_failed_token_save_is_reported_and_the_box_stays_open()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);
        _tokens.SaveThrows = new InvalidOperationException("Credential Manager is unavailable");

        var act = () => settings.SaveToken("new-pat");

        act.Should().NotThrow();
        settings.HasToken.Should().BeFalse();
        settings.IsChangingToken.Should().BeTrue();
        settings.IsTokenBoxShown.Should().BeTrue();
        _tokens.Token.Should().BeNull();
    }

    [Fact]
    public void A_collection_not_found_by_the_user_lookup_points_at_the_collection_url()
    {
        _tokens.Save("pat");
        using var h = Harness(Configured);
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);
        _client.Failures["GetUserAsync"] = new AzureDevOpsFailure(AzureDevOpsFailureKind.NotFound, 404);

        h.Await(settings.TestConnectionAsync());

        (settings.IsTestSuccess, settings.TestResult).Should().Be((false, "Settings_AdoTestCollectionNotFound"));
    }

    [Fact]
    public void A_test_answer_for_values_changed_meanwhile_is_dropped()
    {
        _tokens.Save("pat");
        using var h = Harness(Configured);
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);
        _client.Gate = new TaskCompletionSource();

        var test = settings.TestConnectionAsync();
        settings.Repository = "other";
        _client.Gate.SetResult();
        h.Await(test);

        settings.TestResult.Should().BeNull("the answer was for the old repository");
        settings.IsTesting.Should().BeFalse();

        _client.Gate = null;
        h.Await(settings.TestConnectionAsync());
        settings.IsTestSuccess.Should().BeTrue();
    }

    [Fact]
    public void A_test_answer_for_a_token_changed_meanwhile_is_dropped()
    {
        _tokens.Save("pat");
        using var h = Harness(Configured);
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);
        _client.Gate = new TaskCompletionSource();

        var test = settings.TestConnectionAsync();
        settings.SaveToken("new-pat");
        _client.Gate.SetResult();
        h.Await(test);

        settings.TestResult.Should().BeNull();
    }

    [Fact]
    public void The_collection_url_hint_names_user_info_a_query_or_a_fragment()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);

        settings.CollectionUrl = "https://user:secret@server/value";
        settings.CollectionUrlHint.Should().Be("Settings_AdoCollectionUrlPartsHint");
        settings.CollectionUrl = "https://server/value?x=1";
        settings.CollectionUrlHint.Should().Be("Settings_AdoCollectionUrlPartsHint");
    }

    [Fact]
    public void Test_connection_without_setup_lists_what_is_missing()
    {
        using var h = Harness();
        var settings = new AzureDevOpsSettingsViewModel(h.Workspace.Context);

        h.Await(settings.TestConnectionAsync());

        settings.IsTestSuccess.Should().BeFalse();
        _client.Calls.Should().BeEmpty();
    }

    private static readonly UiPreferences Configured = new()
    {
        AzureDevOps = new AzureDevOpsPreferences { Enabled = true, CollectionUrl = "https://server/value", Project = "ace", Repository = "repo" },
    };

    private WorkspaceHarness Harness(UiPreferences? preferences = null) =>
        WorkspaceHarness.Create(preferences: preferences, azureDevOps: AzureDevOpsTesting.Services(_client, _tokens));
}
