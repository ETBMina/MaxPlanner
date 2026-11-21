using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Application.PullRequests;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Common.PullRequests;

/// <summary>The result bar's severity (design §5.11 Results).</summary>
public enum PullRequestResultSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

public enum PullRequestStepState
{
    Pending,
    Active,
    Done,
}

/// <summary>One line of the progress list (design §5.11 Progress): a ring on the active line, a check on finished lines.</summary>
public sealed partial class PullRequestStepViewModel(PullRequestStep step, string text) : ObservableObject
{
    public PullRequestStep Step { get; } = step;

    public string Text { get; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone))]
    public partial PullRequestStepState State { get; set; }

    public bool IsActive => State == PullRequestStepState.Active;

    public bool IsDone => State == PullRequestStepState.Done;
}

/// <summary>
/// The Create pull request dialog (B-18, ADR 0031, design §5.11). It starts from saved values and the Settings defaults,
/// runs the creation steps, and reports the result in the dialog. Nothing is saved to the dataset, so it does not use the
/// command runner (ADR 0029), and it has no draft to protect: closing never asks. Use on the UI thread.
/// </summary>
public sealed partial class CreatePullRequestViewModel : ObservableObject, IDialogViewModel, IEditorDialogViewModel
{
    /// <summary>The field key that moves focus to the result bar's first action (design §5.11).</summary>
    public const string ResultFocusKey = "Result";

    private readonly PullRequestSource _source;
    private readonly AzureDevOpsServices _services;
    private readonly ILocalizedStrings _strings;
    private readonly IAnnouncer _announcer;
    private readonly IUrlLauncher _urls;
    private readonly AzureDevOpsConnection? _connection;
    private readonly AzureDevOpsPreferences _settings;
    private readonly ObservableCollection<PullRequestStepViewModel> _steps = [];
    private bool _isTitleEdited;
    private bool _isBuildingTitle;
    private bool _isSourceTouched;
    private bool _isTargetTouched;
    private bool _isTitleTouched;

    // An earlier create may have made the pull request unseen (design §5.11): later attempts look for it before posting.
    private bool _mayHaveCreated;

    public CreatePullRequestViewModel(
        PullRequestSource source,
        AzureDevOpsPreferences? settings,
        AzureDevOpsServices services,
        ILocalizedStrings strings,
        IAnnouncer announcer,
        IUrlLauncher urls)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
        _urls = urls ?? throw new ArgumentNullException(nameof(urls));
        _settings = settings ?? AzureDevOpsPreferences.Default;

        // A token missing from Credential Manager counts as missing setup (design §5.11).
        var token = services.Tokens.Read();
        Missing = AzureDevOpsSetup.Missing(_settings, !string.IsNullOrEmpty(token));
        _connection = AzureDevOpsSetup.Connect(_settings, token);

        _isBuildingTitle = true;
        SourceBranch = source.TaskNumber?.Trim() ?? string.Empty;
        TargetBranch = _settings.EffectiveDefaultTarget;
        Title = BuildTitle();
        _isBuildingTitle = false;
        IsAutoComplete = _settings.EffectiveAutoComplete;
        HasNoTaskNumber = string.IsNullOrWhiteSpace(source.TaskNumber);
        Steps = new ReadOnlyObservableCollection<PullRequestStepViewModel>(_steps);

        if (Missing.Count > 0)
        {
            ShowResult(
                PullRequestResultSeverity.Warning,
                _strings.Format("PullRequest_SetupIncomplete", AzureDevOpsText.MissingItems(Missing, _strings)),
                openSettings: true);
        }
    }

    public event EventHandler? CloseRequested;

    public event EventHandler<string>? FocusRequested;

    /// <summary>What Settings still needs; Create stays disabled while anything is missing.</summary>
    public IReadOnlyList<AzureDevOpsSetupItem> Missing { get; }

    public bool IsSetupIncomplete => Missing.Count > 0;

    public string DialogTitle => _strings.GetString("PullRequest_Title");

    /// <summary>"{project} · {repository}" from Settings.</summary>
    public string Caption => _strings.Format("PullRequest_Caption", _settings.Project?.Trim() ?? string.Empty, _settings.Repository?.Trim() ?? string.Empty);

    /// <summary>The dialog's accessible name: "Create pull request for {record title}".</summary>
    public string AccessibleName => _strings.Format("PullRequest_DialogName", _source.RecordTitle);

    /// <summary>The record has no task number, so the source branch starts empty with a helper line.</summary>
    public bool HasNoTaskNumber { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceError), nameof(TargetError), nameof(CanCreate), nameof(IsPrimaryEnabled))]
    public partial string SourceBranch { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceError), nameof(TargetError), nameof(CanCreate), nameof(IsPrimaryEnabled))]
    public partial string TargetBranch { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleError), nameof(CanCreate), nameof(IsPrimaryEnabled))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAutoComplete { get; set; }

    /// <summary>A request is running: fields are locked and Create, Cancel and Esc do nothing (design §5.11 Progress).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaving), nameof(CanEditFields), nameof(CanCreate), nameof(IsPrimaryEnabled), nameof(CloseButtonText))]
    public partial bool IsRunning { get; private set; }

    /// <summary>A pull request exists now: Create became Close and nothing can be retried.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFields), nameof(CanCreate), nameof(IsPrimaryEnabled), nameof(PrimaryButtonText), nameof(CloseButtonText))]
    public partial bool IsCreated { get; private set; }

    /// <summary>An unexpected failure (ADR 0029); shown by the editor message bars and reported app-wide.</summary>
    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? ResultMessage { get; private set; }

    [ObservableProperty]
    public partial PullRequestResultSeverity ResultSeverity { get; private set; }

    /// <summary>The result text before and after the pull request link; the link shows <see cref="LinkText"/>.</summary>
    [ObservableProperty]
    public partial string? ResultBeforeLink { get; private set; }

    [ObservableProperty]
    public partial string? ResultAfterLink { get; private set; }

    /// <summary>"#{id}" while the result names a created pull request; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    public partial string? LinkText { get; private set; }

    /// <summary>"Open pull request #{id} in browser".</summary>
    [ObservableProperty]
    public partial string? LinkName { get; private set; }

    [ObservableProperty]
    public partial bool ShowsOpenExisting { get; private set; }

    [ObservableProperty]
    public partial bool ShowsOpenSettings { get; private set; }

    [ObservableProperty]
    public partial bool ShowsTryAgain { get; private set; }

    /// <summary>Whether the result bar shows any action button; an empty button row would add space below the text.</summary>
    [ObservableProperty]
    public partial bool HasResultActions { get; private set; }

    /// <summary>The created pull request's page; the full address is never shown.</summary>
    public Uri? CreatedUrl { get; private set; }

    /// <summary>Open Settings was chosen: the caller opens Settings › Integrations › Azure DevOps after the dialog closed.</summary>
    public bool IsOpenSettingsRequested { get; private set; }

    public bool HasResult => !string.IsNullOrEmpty(ResultMessage);

    public bool HasLink => !string.IsNullOrEmpty(LinkText);

    public ReadOnlyObservableCollection<PullRequestStepViewModel> Steps { get; }

    public bool HasSteps => _steps.Count > 0;

    public bool CanEditFields => !IsRunning && !IsCreated && !IsSetupIncomplete;

    public bool CanCreate => CanEditFields && _connection is not null && IsValid;

    public bool IsPrimaryEnabled => IsCreated || CanCreate;

    public string PrimaryButtonText => _strings.GetString(IsCreated ? "PullRequest_CloseButton" : "PullRequest_CreateButton");

    /// <summary>Cancel until a pull request exists; then Create became Close and Cancel goes.</summary>
    public string CloseButtonText => IsCreated ? string.Empty : _strings.GetString("Common_Cancel");

    public string HelperText => _strings.GetString("PullRequest_NoTaskNumberHelper");

    public string? SourceError => BranchError(SourceBranch, _isSourceTouched);

    public string? TargetError =>
        BranchError(TargetBranch, _isTargetTouched)
        ?? (IsBranchValid(SourceBranch) && IsBranchValid(TargetBranch) && BranchNames.AreSame(SourceBranch, TargetBranch)
            ? _strings.GetString("PullRequest_SameBranchError")
            : null);

    public string? TitleError => _isTitleTouched && string.IsNullOrWhiteSpace(Title) ? _strings.GetString("PullRequest_TitleRequiredError") : null;

    private bool IsValid =>
        IsBranchValid(SourceBranch) && IsBranchValid(TargetBranch) && !BranchNames.AreSame(SourceBranch, TargetBranch) && !string.IsNullOrWhiteSpace(Title);

    // ----- IEditorDialogViewModel: no draft, so closing never asks (design §5.0); only a running request holds it. -----
    public bool IsSaving => IsRunning;

    public bool IsDirtyClosePromptOpen => false;

    public bool IsWindowClosePending => false;

    public bool TryClose() => !IsRunning;

    public bool TryCloseForWindow() => true;

    public Task<bool> SaveFromPromptAsync() => Task.FromResult(true);

    public void DiscardChanges()
    {
    }

    public void ContinueEditing()
    {
    }

    /// <summary>The primary button, Enter and Ctrl+Enter: Create, or Close once a pull request exists.</summary>
    [RelayCommand]
    private Task Save() => UiTasks.RunGuardedAsync(CreateAsync);

    [RelayCommand]
    private Task TryAgain() => UiTasks.RunGuardedAsync(CreateAsync);

    /// <summary>The #id link: opens the pull request page in the default browser.</summary>
    [RelayCommand]
    private Task OpenPullRequest() => UiTasks.RunGuardedAsync(() => CreatedUrl is { } url ? _urls.OpenAsync(url) : Task.CompletedTask);

    /// <summary>Open existing PR: finds the active pull request from the source to the target and opens it.</summary>
    [RelayCommand]
    private Task OpenExisting() => UiTasks.RunGuardedAsync(OpenExistingAsync);

    /// <summary>Closes the dialog; the caller then opens Settings with the Azure DevOps expander expanded and focused.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        if (IsRunning)
        {
            return;
        }

        IsOpenSettingsRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task CreateAsync()
    {
        if (IsCreated)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsRunning || !CanCreate || _connection is not { } connection)
        {
            return;
        }

        ClearResult();
        SaveError = null;
        _steps.Clear();
        foreach (var step in Enum.GetValues<PullRequestStep>().Where(s => IsAutoComplete || s != PullRequestStep.SettingAutoComplete))
        {
            _steps.Add(new PullRequestStepViewModel(step, StepText(step)));
        }

        OnPropertyChanged(nameof(HasSteps));
        IsRunning = true;
        try
        {
            var outcome = await new CreatePullRequest(_services.Client)
                .ExecuteAsync(connection, new PullRequestRequest(SourceBranch, TargetBranch, Title, IsAutoComplete, _mayHaveCreated), OnStep, CancellationToken.None)
                .ConfigureAwait(true);
            _mayHaveCreated |= outcome.Kind == PullRequestOutcomeKind.CreateOutcomeUnknown;
            ShowOutcome(outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Reported app-wide by UiTasks; the dialog keeps its values and can try again.
            SaveError = _strings.GetString("Error_Unexpected");
            throw;
        }
        finally
        {
            IsRunning = false;
            _steps.Clear();
            OnPropertyChanged(nameof(HasSteps));
        }

        if (HasResult)
        {
            FocusRequested?.Invoke(this, ResultFocusKey);
        }
    }

    partial void OnSourceBranchChanged(string value)
    {
        _isSourceTouched |= !_isBuildingTitle;
        RebuildTitle();
    }

    partial void OnTargetBranchChanged(string value)
    {
        _isTargetTouched |= !_isBuildingTitle;
        RebuildTitle();
    }

    partial void OnTitleChanged(string value)
    {
        if (!_isBuildingTitle)
        {
            // Design §5.11: once the user edits the title it is kept.
            _isTitleEdited = true;
            _isTitleTouched = true;
        }
    }

    private static bool IsBranchValid(string? value) => BranchNames.Check(value) == BranchNameProblem.None;

    private string? BranchError(string? value, bool isTouched) => BranchNames.Check(value) switch
    {
        BranchNameProblem.Invalid => _strings.GetString("PullRequest_BranchInvalidError"),
        BranchNameProblem.Empty when isTouched => _strings.GetString("PullRequest_BranchRequiredError"),
        _ => null,
    };

    private string BuildTitle() => PullRequestTitleFormatter.Apply(_settings.TitleTemplate, _source.TitleValues(SourceBranch, TargetBranch));

    private void RebuildTitle()
    {
        if (_isTitleEdited)
        {
            return;
        }

        var wasBuilding = _isBuildingTitle;
        _isBuildingTitle = true;
        Title = BuildTitle();
        _isBuildingTitle = wasBuilding;
    }

    private string StepText(PullRequestStep step) => _strings.GetString("PullRequest_Step" + step);

    private void OnStep(PullRequestStep step)
    {
        foreach (var line in _steps)
        {
            if (line.Step == step)
            {
                line.State = PullRequestStepState.Active;
                _announcer.Announce(line.Text);
            }
            else if (line.State == PullRequestStepState.Active)
            {
                line.State = PullRequestStepState.Done;
            }
        }
    }

    private async Task OpenExistingAsync()
    {
        if (IsRunning || _connection is not { } connection)
        {
            return;
        }

        IsRunning = true;
        AzureDevOpsResult<int?> found;
        try
        {
            found = await _services.Client.FindActivePullRequestAsync(connection, SourceBranch, TargetBranch, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
        }

        if (found is { IsSuccess: true, Value: { } id })
        {
            await _urls.OpenAsync(PullRequestLinks.WebUrl(connection.CollectionUrl, connection.Project, connection.Repository, id)).ConfigureAwait(true);
            return;
        }

        var message = found.Failure is { } failure
            ? _strings.Format("PullRequest_ServerError", AzureDevOpsText.ServerMessage(failure, _strings))
            : _strings.GetString("PullRequest_ExistingNotFound");
        ShowResult(PullRequestResultSeverity.Error, message, openExisting: true, tryAgain: _mayHaveCreated);
        FocusRequested?.Invoke(this, ResultFocusKey);
    }

    private void ShowOutcome(PullRequestOutcome outcome)
    {
        var source = BranchNames.ToShortName(SourceBranch);
        var target = BranchNames.ToShortName(TargetBranch);
        switch (outcome.Kind)
        {
            case PullRequestOutcomeKind.Created or PullRequestOutcomeKind.CreatedAutoCompleteFailed when outcome.PullRequestId is { } id && _connection is { } connection:
                CreatedUrl = PullRequestLinks.WebUrl(connection.CollectionUrl, connection.Project, connection.Repository, id);
                IsCreated = true;
                var isWarning = outcome.Kind == PullRequestOutcomeKind.CreatedAutoCompleteFailed;
                ShowLinkedResult(
                    isWarning ? PullRequestResultSeverity.Warning : PullRequestResultSeverity.Success,
                    _strings.GetString(isWarning ? "PullRequest_CreatedAutoCompleteFailed" : "PullRequest_Created"),
                    id,
                    AzureDevOpsText.ServerMessage(outcome.Failure, _strings));
                break;
            case PullRequestOutcomeKind.NoNewCommits:
                ShowResult(PullRequestResultSeverity.Warning, _strings.Format("PullRequest_NoNewCommits", source, target));
                break;
            case PullRequestOutcomeKind.BranchNotFound:
                ShowResult(PullRequestResultSeverity.Error, _strings.Format("PullRequest_BranchNotFound", outcome.Branch ?? source, _connection?.Repository ?? string.Empty));
                break;
            case PullRequestOutcomeKind.AlreadyExists:
                ShowResult(PullRequestResultSeverity.Informational, _strings.Format("PullRequest_AlreadyExists", source, target), openExisting: true);
                break;
            case PullRequestOutcomeKind.TokenRejected:
                ShowResult(PullRequestResultSeverity.Error, _strings.GetString("PullRequest_TokenRejected"), openSettings: true);
                break;
            case PullRequestOutcomeKind.Unreachable:
                ShowResult(
                    PullRequestResultSeverity.Error,
                    _strings.Format("PullRequest_Unreachable", AzureDevOpsText.ServerName(_connection?.CollectionUrl), AzureDevOpsText.Reason(outcome.Failure!, _strings)),
                    tryAgain: true);
                break;
            case PullRequestOutcomeKind.CreateOutcomeUnknown:
                // Try again looks for the pull request before posting again (_mayHaveCreated).
                ShowResult(PullRequestResultSeverity.Warning, _strings.GetString("PullRequest_OutcomeUnknown"), openExisting: true, tryAgain: true);
                break;
            default:
                ShowResult(PullRequestResultSeverity.Error, _strings.Format("PullRequest_ServerError", AzureDevOpsText.ServerMessage(outcome.Failure, _strings)), tryAgain: true);
                break;
        }
    }

    // "Pull request {0} created." with {0} as the #id link; {1} is the server message where the template has one.
    private void ShowLinkedResult(PullRequestResultSeverity severity, string template, int id, string serverMessage)
    {
        var link = "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (before, after) = LinkedMessage.Split(template, link, serverMessage);
        ResultBeforeLink = before;
        ResultAfterLink = after;
        LinkText = link;
        LinkName = _strings.Format("PullRequest_LinkName", link);
        SetResult(severity, before + link + after, openExisting: false, openSettings: false, tryAgain: false);
    }

    private void ShowResult(PullRequestResultSeverity severity, string message, bool openExisting = false, bool openSettings = false, bool tryAgain = false)
    {
        ResultBeforeLink = message;
        ResultAfterLink = null;
        LinkText = null;
        LinkName = null;
        SetResult(severity, message, openExisting, openSettings, tryAgain);
    }

    private void SetResult(PullRequestResultSeverity severity, string message, bool openExisting, bool openSettings, bool tryAgain)
    {
        ResultSeverity = severity;
        ShowsOpenExisting = openExisting;
        ShowsOpenSettings = openSettings;

        // Design §5.11: Try again only while nothing was created.
        ShowsTryAgain = tryAgain && !IsCreated;
        HasResultActions = ShowsOpenExisting || ShowsOpenSettings || ShowsTryAgain;
        ResultMessage = message;
        _announcer.Announce(message, isFailure: severity == PullRequestResultSeverity.Error);
    }

    private void ClearResult()
    {
        ResultMessage = null;
        ResultBeforeLink = null;
        ResultAfterLink = null;
        LinkText = null;
        LinkName = null;
        ShowsOpenExisting = false;
        ShowsOpenSettings = false;
        ShowsTryAgain = false;
        HasResultActions = false;
    }
}

/// <summary>Splits a message template around its link placeholder <c>{0}</c>, so the view can show the link inline.</summary>
public static class LinkedMessage
{
    /// <param name="template">A composite format whose <c>{0}</c> is the link; other placeholders take <paramref name="args"/> from index 1.</param>
    public static (string Before, string After) Split(string template, string link, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(template);
        var all = new object?[] { link }.Concat(args).ToArray();
        var index = template.IndexOf("{0}", StringComparison.Ordinal);
        if (index < 0)
        {
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture, template, all), string.Empty);
        }

        return (
            string.Format(System.Globalization.CultureInfo.CurrentCulture, template[..index], all),
            string.Format(System.Globalization.CultureInfo.CurrentCulture, template[(index + 3)..], all));
    }
}
