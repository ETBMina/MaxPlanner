using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Shell;

[Trait("Req", "T-09")]
[Trait("Req", "AC-15")]
public sealed class RecoveryViewModelTests
{
    private const string DataDirectory = @"C:\Max Planner Tëst\data";
    private const string BackupsDirectory = @"C:\Max Planner Tëst\data\backups";
    private readonly FakeRecoveryActions _actions = new();
    private readonly FakeDataFolder _dataFolder = new();

    [Fact]
    public async Task Creating_an_empty_dataset_requires_an_explicit_confirmation()
    {
        var recovery = New(RecoveryKind.DatabaseMissing);

        recovery.CanCreateNew.Should().BeTrue();
        recovery.IsCreateOfferVisible.Should().BeTrue();
        recovery.ConfirmCreateCommand.CanExecute(null).Should().BeFalse("nothing can be confirmed before the offer is taken");

        recovery.BeginCreateCommand.Execute(null);

        _actions.CreateCount.Should().Be(0, "starting the confirmation creates nothing");
        recovery.IsConfirmingCreate.Should().BeTrue();
        recovery.IsCreateOfferVisible.Should().BeFalse();

        await recovery.ConfirmCreateCommand.ExecuteAsync(null);

        _actions.CreateCount.Should().Be(1);
        recovery.HasActionError.Should().BeFalse();
    }

    [Fact]
    public void Cancelling_the_confirmation_creates_nothing()
    {
        var recovery = New(RecoveryKind.DatabaseMissing);
        recovery.BeginCreateCommand.Execute(null);

        recovery.CancelCreateCommand.Execute(null);

        recovery.IsConfirmingCreate.Should().BeFalse();
        recovery.IsCreateOfferVisible.Should().BeTrue();
        _actions.CreateCount.Should().Be(0);
    }

    [Theory]
    [InlineData(CreateDatasetOutcome.AlreadyExists, "Recovery_CreateAlreadyExists")]
    [InlineData(CreateDatasetOutcome.Failed, "Recovery_CreateFailed")]
    public async Task A_create_that_did_not_happen_is_explained_and_the_offer_returns(CreateDatasetOutcome outcome, string expectedKey)
    {
        var recovery = New(RecoveryKind.DatabaseMissing);
        _actions.NextCreateOutcome = outcome;
        recovery.BeginCreateCommand.Execute(null);

        await recovery.ConfirmCreateCommand.ExecuteAsync(null);

        recovery.ActionError.Should().Be(expectedKey);
        recovery.IsConfirmingCreate.Should().BeFalse();
        recovery.IsBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(RecoveryKind.Corrupt)]
    [InlineData(RecoveryKind.UnsupportedSchema)]
    [InlineData(RecoveryKind.MigrationFailed)]
    [InlineData(RecoveryKind.InterruptedRestore)]
    [InlineData(RecoveryKind.StorageUnavailable)]
    public void Only_a_missing_database_offers_creating_a_new_one(RecoveryKind kind)
    {
        var recovery = New(kind);

        recovery.CanCreateNew.Should().BeFalse();
        recovery.IsCreateOfferVisible.Should().BeFalse();
        recovery.BeginCreateCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Retry_is_offered_only_for_storage_that_was_unavailable()
    {
        New(RecoveryKind.Corrupt).RetryCommand.CanExecute(null).Should().BeFalse();
        var recovery = New(RecoveryKind.StorageUnavailable, storage: StorageFailureKind.FileLocked);
        recovery.CanRetry.Should().BeTrue();

        await recovery.RetryCommand.ExecuteAsync(null);

        _actions.RetryCount.Should().Be(1);
    }

    [Fact]
    public void Exit_and_open_data_folder_are_always_available()
    {
        var recovery = New(RecoveryKind.UnsupportedSchema);

        recovery.OpenDataFolderCommand.Execute(null);
        recovery.ExitCommand.Execute(null);

        _dataFolder.OpenCount.Should().Be(1);
        _actions.CloseCount.Should().Be(1);
        recovery.DataFolderPath.Should().Be(DataDirectory);
    }

    [Fact]
    public void Messages_name_the_versions_backup_folder_and_failure()
    {
        var strings = new FakeStrings()
            .With("Recovery_UnsupportedSchema_Message", "found {0}, supports {1}")
            .With("Recovery_MigrationFailed_Message", "still {0}, copy in {1}")
            .With("Recovery_Corrupt_Message", "backups in {0}")
            .With("Recovery_StorageUnavailable_Message", "{0} Retry.")
            .With("StorageFailure_FileLocked", "Locked.");

        New(RecoveryKind.UnsupportedSchema, strings, found: 7, supported: 1).Message.Should().Be("found 7, supports 1");
        New(RecoveryKind.MigrationFailed, strings, current: 1, safety: @"C:\b\x-migration-safety").Message.Should().Be(@"still 1, copy in C:\b\x-migration-safety");
        New(RecoveryKind.Corrupt, strings).Message.Should().Be("backups in " + BackupsDirectory);
        New(RecoveryKind.StorageUnavailable, strings, storage: StorageFailureKind.FileLocked).Message.Should().Be("Locked. Retry.");
        New(RecoveryKind.Corrupt, strings).Title.Should().Be("Recovery_Corrupt_Title");
    }

    [Theory]
    [InlineData(RecoveryKind.UnsupportedSchema, true)]
    [InlineData(RecoveryKind.InterruptedRestore, true)]
    [InlineData(RecoveryKind.Corrupt, false)]
    [InlineData(RecoveryKind.DatabaseMissing, false)]
    public void Damage_is_an_error_while_a_newer_dataset_or_pending_restore_is_a_warning(RecoveryKind kind, bool isWarning)
    {
        New(kind).IsWarning.Should().Be(isWarning);
    }

    [Fact]
    public void Open_failures_map_to_their_recovery_kind()
    {
        Map(new UnsupportedSchemaError(5, 1)).Should().Be(new RecoveryInfo(RecoveryKind.UnsupportedSchema, DataDirectory, BackupsDirectory, FoundSchemaVersion: 5, SupportedSchemaVersion: 1));
        Map(new MigrationFailedError(1, 2), @"C:\safety").Should().Be(new RecoveryInfo(RecoveryKind.MigrationFailed, DataDirectory, BackupsDirectory, CurrentSchemaVersion: 1, SafetyBackupDirectory: @"C:\safety"));
        Map(new StorageFailureError(StorageFailureKind.Corrupt)).Kind.Should().Be(RecoveryKind.Corrupt);
        Map(new StorageFailureError(StorageFailureKind.Unavailable)).Should().Be(new RecoveryInfo(RecoveryKind.StorageUnavailable, DataDirectory, BackupsDirectory, StorageFailure: StorageFailureKind.Unavailable));
    }

    private static RecoveryInfo Map(AppError error, string? safety = null) =>
        RecoveryInfo.ForOpenFailure(error, DataDirectory, BackupsDirectory, safety);

    private RecoveryViewModel New(
        RecoveryKind kind,
        FakeStrings? strings = null,
        StorageFailureKind? storage = null,
        int? found = null,
        int? supported = null,
        int? current = null,
        string? safety = null) =>
        new(new RecoveryInfo(kind, DataDirectory, BackupsDirectory, storage, found, supported, current, safety), strings ?? new FakeStrings(), _actions, _dataFolder);
}
