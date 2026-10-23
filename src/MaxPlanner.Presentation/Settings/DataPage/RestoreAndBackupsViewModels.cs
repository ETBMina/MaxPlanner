using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Settings.DataPage;

/// <summary>A backup folder row (Manage backups, Restore step 1): kind, date, size, and manifest state as text.</summary>
public sealed class BackupRowViewModel(BackupListItem item, ILocalizedStrings strings)
{
    public BackupListItem Item { get; } = item;

    public string Name => Item.Name;

    public string Path => Item.Path;

    public bool IsManual => Item.IsManual;

    public string KindText { get; } = strings.GetString("BackupKind_" + item.Kind.Replace("-", string.Empty, StringComparison.Ordinal));

    public string DateText { get; } = item.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string SizeText { get; } = strings.Format("Backups_Size", (item.SizeBytes / 1024.0 / 1024.0).ToString("N1", CultureInfo.CurrentCulture));

    public string ManifestText { get; } = strings.GetString("Backups_Manifest_" + item.ManifestState);

    public string AccessibleName => $"{KindText}, {DateText}, {SizeText}, {ManifestText}";
}

/// <summary>Manage backups (T-09): list, Back up now, open folder, and confirmed deletion of manual backups only.</summary>
public sealed partial class BackupsViewModel(WorkspaceContext context, DataServices services) : ObservableObject
{
    public ObservableCollection<BackupRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public string FolderPath => services.Maintenance.BackupsDirectory;

    /// <summary>Raised after the backup list changed (Settings shows the newest backup).</summary>
    public event EventHandler? BackupsChanged;

    private bool CanAct => !IsBusy;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await services.Maintenance.ListBackupsAsync(CancellationToken.None).ConfigureAwait(true);
        Rows.Clear();
        foreach (var item in items)
        {
            Rows.Add(new BackupRowViewModel(item, context.Strings));
        }

        StatusText = context.Strings.Format("Backups_Count", Rows.Count);
    }

    /// <summary>T-09: a manual backup, never pruned by retention.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task BackUpNowAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await services.Maintenance.CreateBackupAsync("manual", CancellationToken.None).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ErrorMessage = CommandErrorText.Describe(result.Error!, "Record_Backup", context.Strings);
                return;
            }

            context.Announce(context.Strings.GetString("Backups_Created"));
            await RefreshAsync().ConfigureAwait(true);
            BackupsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder() => services.Maintenance.OpenBackupsFolder();

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteAsync(BackupRowViewModel? row)
    {
        if (row is not { IsManual: true })
        {
            return;
        }

        var strings = context.Strings;
        var confirmation = new ConfirmationViewModel(
            "DeleteBackup_Dialog",
            strings.GetString("Backups_DeleteTitle"),
            strings.Format("Backups_DeleteMessage", row.DateText, row.SizeText),
            strings.GetString("Backups_DeletePrimary"),
            null,
            strings.GetString("Common_Cancel"));
        await context.ShowDialogAsync(confirmation).ConfigureAwait(true);
        if (confirmation.Result != ConfirmationResult.Primary)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!await services.Maintenance.DeleteManualBackupAsync(row.Path, CancellationToken.None).ConfigureAwait(true))
            {
                ErrorMessage = strings.GetString("Backups_DeleteFailed");
            }

            await RefreshAsync().ConfigureAwait(true);
            BackupsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// Restore (T-09, design §4.6): Choose backup → Validate (staged copy: schema, integrity, foreign keys, checksums,
/// records) → Confirm with an explicit checkbox → Restore (safety backup, close, marker-guarded swap, reopen).
/// </summary>
public sealed partial class RestoreWizardViewModel : WizardViewModel
{
    private const int ChooseStep = 0;
    private const int ValidateStep = 1;
    private const int ConfirmStep = 2;
    private const int ResultStep = 3;

    private readonly DataServices _services;
    private RestorePreparation? _preparation;
    private bool _isRestoring;

    public RestoreWizardViewModel(ILocalizedStrings strings, DataServices services)
        : base(strings, ["Restore_Step_Choose", "Restore_Step_Validate", "Restore_Step_Confirm", "Restore_Step_Result"])
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderText))]
    public partial BackupRowViewModel? SelectedBackup { get; set; }

    /// <summary>A folder chosen with Browse… (a backup copied from elsewhere).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderText))]
    public partial string? BrowsedFolder { get; private set; }

    public string SelectedFolderText => SelectedFolder is { } folder ? Strings.Format("Restore_SelectedFolder", folder) : Strings.GetString("Restore_NoneSelected");

    [ObservableProperty]
    public partial IReadOnlyList<RestoreCheckRow> Checks { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsConfirmed { get; set; }

    [ObservableProperty]
    public partial string ResultText { get; private set; } = string.Empty;

    public override string NextText => Strings.GetString(StepIndex == ConfirmStep ? "Restore_RestoreButtonText" : "Data_NextButtonText");

    protected override bool CanAdvance => StepIndex switch
    {
        ChooseStep => SelectedFolder is not null,
        ValidateStep => _preparation?.IsValid == true,
        ConfirmStep => IsConfirmed,
        _ => false,
    };

    private string? SelectedFolder => BrowsedFolder ?? SelectedBackup?.Path;

    public async Task RefreshAsync()
    {
        var items = await _services.Maintenance.ListBackupsAsync(CancellationToken.None).ConfigureAwait(true);
        Backups.Clear();
        foreach (var item in items)
        {
            Backups.Add(new BackupRowViewModel(item, Strings));
        }
    }

    public override void Dispose()
    {
        // A running restore owns the staged copy: the session (and this page) closes before the swap uses it.
        if (_preparation is { } preparation && StepIndex < ResultStep && !_isRestoring)
        {
            _services.Maintenance.DiscardRestore(preparation);
        }

        base.Dispose();
    }

    partial void OnSelectedBackupChanged(BackupRowViewModel? value)
    {
        if (value is not null)
        {
            BrowsedFolder = null;
        }

        RefreshCommands();
    }

    partial void OnIsConfirmedChanged(bool value) => RefreshCommands();

    protected override int PreviousStep(int current)
    {
        if (current == ValidateStep && _preparation is { } preparation)
        {
            _services.Maintenance.DiscardRestore(preparation);
            _preparation = null;
        }

        IsConfirmed = false;
        return current - 1;
    }

    protected override async Task<int?> AdvanceAsync(CancellationToken cancellationToken)
    {
        switch (StepIndex)
        {
            case ChooseStep:
                _preparation = await _services.Maintenance.PrepareRestoreAsync(SelectedFolder!, cancellationToken).ConfigureAwait(true);
                Checks = [.. _preparation.Checks.Select(c => new RestoreCheckRow(
                    c.Passed ? "" : "",
                    Strings.Format(c.Passed ? "Restore_CheckPassed" : "Restore_CheckFailed", Strings.GetString("Restore_Check_" + c.Kind), c.Detail ?? string.Empty)))];
                if (!_preparation.IsValid)
                {
                    ErrorMessage = Strings.GetString("Restore_Invalid");
                }

                return ValidateStep;
            case ValidateStep:
                return ConfirmStep;
            case ConfirmStep:
                var preparation = _preparation!;
                _isRestoring = true;
                var outcome = await _services.Maintenance.RestoreAsync(preparation).ConfigureAwait(true);
                _isRestoring = false;

                // On success the dataset was reopened and this page belongs to the closed session; the shell reports it.
                ResultText = Strings.GetString("Restore_Outcome_" + outcome);
                return ResultStep;
            default:
                return null;
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _services.Pickers.PickFolderAsync().ConfigureAwait(true) is { } folder)
        {
            SelectedBackup = null;
            BrowsedFolder = folder;
            RefreshCommands();
        }
    }
}

/// <param name="Glyph">Check mark or cross, shown with the pass/fail text (never color alone).</param>
public sealed record RestoreCheckRow(string Glyph, string Text);
