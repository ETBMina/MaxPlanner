using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.PullRequests;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings;

/// <summary>
/// Settings › Integrations › Azure DevOps (ADR 0031, design §4.6). The values are per-PC UI preferences that save on
/// change; the token lives only in Windows Credential Manager and is never held here after it was saved. Use on the UI thread.
/// </summary>
public sealed partial class AzureDevOpsSettingsViewModel : ObservableObject
{
    private readonly WorkspaceContext _context;
    private readonly bool _isLoading = true;

    // Bumped by every change to the values or the token, so a Test connection answer for older values is dropped.
    private int _version;

    public AzureDevOpsSettingsViewModel(WorkspaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var saved = context.Preferences.Current.AzureDevOps ?? AzureDevOpsPreferences.Default;
        IsEnabled = saved.Enabled;
        CollectionUrl = saved.CollectionUrl ?? string.Empty;
        Project = saved.Project ?? string.Empty;
        Repository = saved.Repository ?? string.Empty;
        DefaultTarget = saved.DefaultTarget ?? string.Empty;
        IsAutoComplete = saved.EffectiveAutoComplete;
        TitleTemplate = saved.TitleTemplate ?? string.Empty;
        HasToken = ReadHasToken(context);
        _isLoading = false;
    }

    /// <summary>Asks the view to focus the token box (after Change…).</summary>
    public event EventHandler? TokenBoxFocusRequested;

    /// <summary>The integration switch (off by default). Off hides every pull request command; values and token are kept.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>The expander is open; it opens with the switch and for Open Settings (design §5.11).</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectionUrlHint))]
    public partial string CollectionUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Project { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Repository { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAutoComplete { get; set; }

    [ObservableProperty]
    public partial string TitleTemplate { get; set; } = string.Empty;

    /// <summary>A token is saved in Credential Manager; the page shows only "Saved".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTokenBoxShown), nameof(IsSavedTokenShown))]
    public partial bool HasToken { get; private set; }

    /// <summary>Change… was chosen: the token box replaces "Saved" until a new token is saved or the change is dropped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTokenBoxShown), nameof(IsSavedTokenShown))]
    public partial bool IsChangingToken { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    public partial bool IsTesting { get; private set; }

    /// <summary>The inline Test connection result; null before a test or after a change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    public partial bool IsTestSuccess { get; private set; }

    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);

    public bool CanTestConnection => !IsTesting;

    public bool IsTokenBoxShown => !HasToken || IsChangingToken;

    public bool IsSavedTokenShown => HasToken && !IsChangingToken;

    /// <summary>The inline hint under the collection URL (design §4.6); null while the address is usable or empty.</summary>
    public string? CollectionUrlHint => CollectionUrls.Check(CollectionUrl) switch
    {
        CollectionUrlProblem.NotHttps => _context.Strings.GetString("Settings_AdoCollectionUrlHttpsHint"),
        CollectionUrlProblem.NotCollection => _context.Strings.GetString("Settings_AdoCollectionUrlCollectionHint"),
        CollectionUrlProblem.ExtraParts => _context.Strings.GetString("Settings_AdoCollectionUrlPartsHint"),
        _ => null,
    };

    public static string TitlePlaceholder => PullRequestTitleFormatter.Default;

    public static string DefaultTargetPlaceholder => AzureDevOpsPreferences.DefaultTargetBranch;

    /// <summary>
    /// Saves a typed token (Enter or leaving the box). A blank box keeps the saved token. A Credential Manager failure is
    /// reported (ADR 0029) and the box stays open, so the user can try again; it never reaches the view's event handler.
    /// </summary>
    public void SaveToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            IsChangingToken = false;
            return;
        }

        _version++;
        try
        {
            _context.AzureDevOps.Tokens.Save(token.Trim());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiTasks.Report(ex, _context);
            IsChangingToken = true;
            return;
        }

        HasToken = true;
        IsChangingToken = false;
        TestResult = null;
        _context.Announce(_context.Strings.GetString("Settings_AdoTokenSavedAnnouncement"));
    }

    [RelayCommand]
    private void ChangeToken()
    {
        IsChangingToken = true;
        TokenBoxFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remove asks first (design §4.6); the entry is deleted from Credential Manager.</summary>
    [RelayCommand]
    private Task RemoveToken() => UiTasks.RunGuardedAsync(RemoveTokenAsync);

    [RelayCommand]
    private Task TestConnection() => UiTasks.RunGuardedAsync(TestConnectionAsync);

    public async Task RemoveTokenAsync()
    {
        var strings = _context.Strings;
        var confirmation = new ConfirmationViewModel(
            "RemoveAdoToken_Dialog",
            strings.GetString("Settings_AdoRemoveTokenTitle"),
            strings.GetString("Settings_AdoRemoveTokenMessage"),
            strings.GetString("Settings_AdoRemoveTokenButton"),
            null,
            strings.GetString("Common_Cancel"));
        await _context.ShowDialogAsync(confirmation).ConfigureAwait(true);
        if (confirmation.Result != ConfirmationResult.Primary)
        {
            return;
        }

        _version++;
        _context.AzureDevOps.Tokens.Remove();
        HasToken = false;
        IsChangingToken = false;
        TestResult = null;
        _context.Announce(strings.GetString("Settings_AdoTokenRemovedAnnouncement"));
    }

    /// <summary>Checks the server, the token, the project and the repository (B-18.3).</summary>
    public async Task TestConnectionAsync()
    {
        if (IsTesting)
        {
            return;
        }

        var strings = _context.Strings;
        var settings = Current;
        var token = _context.AzureDevOps.Tokens.Read();
        if (AzureDevOpsSetup.Connect(settings, token) is not { } connection)
        {
            var missing = AzureDevOpsSetup.Missing(settings, !string.IsNullOrEmpty(token));
            ShowTest(false, strings.Format("PullRequest_SetupIncomplete", AzureDevOpsText.MissingItems(missing, strings)));
            return;
        }

        IsTesting = true;
        TestResult = null;
        var version = _version;
        try
        {
            var client = _context.AzureDevOps.Client;
            var user = await client.GetUserAsync(connection, CancellationToken.None).ConfigureAwait(true);
            if (version != _version)
            {
                return; // The values or the token changed while the test ran; its answer is for the old ones.
            }

            if (user.Failure is { } userFailure)
            {
                ShowTest(false, FailureText(userFailure, connection, isUserLookup: true));
                return;
            }

            var repository = await client.GetRepositoryAsync(connection, CancellationToken.None).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            ShowTest(
                repository.IsSuccess,
                repository.Failure is { } repositoryFailure
                    ? FailureText(repositoryFailure, connection, isUserLookup: false)
                    : strings.Format("Settings_AdoTestConnected", user.Value!.DisplayName, repository.Value));
        }
        finally
        {
            IsTesting = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        // The switch only controls whether the Create pull request commands appear; the rows stay editable when off.
        Save(p => p with { Enabled = value });
    }

    partial void OnCollectionUrlChanged(string value) => Save(p => p with { CollectionUrl = Text(value) });

    partial void OnProjectChanged(string value) => Save(p => p with { Project = Text(value) });

    partial void OnRepositoryChanged(string value) => Save(p => p with { Repository = Text(value) });

    partial void OnDefaultTargetChanged(string value) => Save(p => p with { DefaultTarget = Text(value) });

    partial void OnIsAutoCompleteChanged(bool value) => Save(p => p with { AutoComplete = value });

    partial void OnTitleTemplateChanged(string value) => Save(p => p with { TitleTemplate = Text(value) });

    // A Credential Manager failure other than "no entry" must not keep the Settings page from opening (ADR 0029 report).
    private static bool ReadHasToken(WorkspaceContext context)
    {
        try
        {
            return !string.IsNullOrEmpty(context.AzureDevOps.Tokens.Read());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiTasks.Report(ex, context);
            return false;
        }
    }

    private AzureDevOpsPreferences Current => _context.Preferences.Current.AzureDevOps ?? AzureDevOpsPreferences.Default;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Save(Func<AzureDevOpsPreferences, AzureDevOpsPreferences> change)
    {
        if (_isLoading)
        {
            return;
        }

        _version++;
        _context.Preferences.Update(p => p with { AzureDevOps = change(p.AzureDevOps ?? AzureDevOpsPreferences.Default) });
        TestResult = null;
    }

    /// <param name="isUserLookup">
    /// The failure came from <c>_apis/connectionData</c>, which names neither the project nor the repository: a 404 there
    /// means the collection address is wrong.
    /// </param>
    private string FailureText(AzureDevOpsFailure failure, AzureDevOpsConnection connection, bool isUserLookup)
    {
        var strings = _context.Strings;
        return failure.Kind switch
        {
            AzureDevOpsFailureKind.Unauthorized => strings.GetString("PullRequest_TokenRejected"),
            AzureDevOpsFailureKind.NotFound when isUserLookup => strings.GetString("Settings_AdoTestCollectionNotFound"),
            AzureDevOpsFailureKind.NotFound => strings.Format("Settings_AdoTestNotFound", connection.Project, connection.Repository),
            AzureDevOpsFailureKind.Timeout or AzureDevOpsFailureKind.Network =>
                strings.Format("PullRequest_Unreachable", AzureDevOpsText.ServerName(connection.CollectionUrl), AzureDevOpsText.Reason(failure, strings)),
            _ => strings.Format("PullRequest_ServerError", AzureDevOpsText.ServerMessage(failure, strings)),
        };
    }

    private void ShowTest(bool isSuccess, string text)
    {
        IsTestSuccess = isSuccess;
        TestResult = text;
        _context.Announce(text, isFailure: !isSuccess);
    }
}
