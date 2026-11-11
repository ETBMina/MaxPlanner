using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MaxPlanner.Application.Tests.Startup;

/// <summary>
/// The startup, recovery, and restore decisions of <see cref="StartupCoordinator"/> (T-09, AC-15, ADR 0015) over fake
/// ports. The call log pins the order of file operations and shell steps.
/// </summary>
[Trait("Req", "T-09")]
public sealed class StartupCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly CallLog _log = new();
    private readonly FakeDatasetStorage _storage;
    private readonly FakeRestoreOperations _restores;
    private readonly FakeSessionFactory _sessions;
    private readonly FakeStartupShell _shell;
    private bool _hasPriorUse = true;

    public StartupCoordinatorTests()
    {
        _storage = new FakeDatasetStorage(_log);
        _restores = new FakeRestoreOperations(_log, _storage);
        _sessions = new FakeSessionFactory(_log);
        _shell = new FakeStartupShell(_log);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task First_run_with_no_dataset_and_no_prior_use_creates_and_opens_a_new_dataset()
    {
        _hasPriorUse = false;
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => _storage.Opened("new"));
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal("shell.starting", "storage.marker?", "storage.open", "storage.create", "session.create:new", "session.attach:new", "session.start:new");
        coordinator.Recovery.Should().BeNull();
        coordinator.Exit.Should().Be(StartupExit.Ok);
    }

    [Fact]
    public async Task First_run_opens_the_files_that_appeared_after_the_inspection_instead_of_creating()
    {
        _hasPriorUse = false;
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => new DatasetOpenAttempt(DatasetOpenOutcome.AlreadyExists, null, null));
        _storage.Opens.Enqueue(() => _storage.Opened("appeared"));
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal(
            "shell.starting", "storage.marker?", "storage.open", "storage.create", "storage.open", "session.create:appeared", "session.attach:appeared", "session.start:appeared");
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task A_missing_database_in_a_used_folder_shows_recovery_and_never_creates_an_empty_dataset()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal("shell.starting", "storage.marker?", "storage.open", "shell.recovery:DatabaseMissing");
        coordinator.Recovery.Should().Be(new StartupRecovery(StartupRecoveryKind.DatabaseMissing));
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task Normal_open_starts_the_session_and_runs_the_daily_backup_only_after_the_first_ready_view()
    {
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal("shell.starting", "storage.marker?", "storage.open", "session.create:dataset", "session.attach:dataset", "session.start:dataset");
        _sessions.Current.FakeBackups.DailyRuns.Should().Be(0, "the backup never delays startup");

        _sessions.Current.BecomeReady();

        _sessions.Current.FakeBackups.DailyRuns.Should().Be(1);
        _shell.BackupFailures.Should().BeEmpty();
        coordinator.Exit.Should().Be(StartupExit.Ok);
    }

    [Fact]
    public async Task An_open_failure_shows_recovery_for_the_typed_error_with_the_migration_safety_backup()
    {
        var error = new MigrationFailedError(3, 4);
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(error, @"C:\data\backups\x-migration-safety"));
        using var coordinator = Create();

        await coordinator.RunAsync();

        _sessions.Sessions.Should().BeEmpty();
        coordinator.Recovery.Should().Be(new StartupRecovery(StartupRecoveryKind.OpenFailed, error, @"C:\data\backups\x-migration-safety"));
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task An_open_failure_without_an_error_is_treated_as_corrupt()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(null));
        using var coordinator = Create();

        await coordinator.RunAsync();

        coordinator.Recovery!.Error.Should().Be(new StorageFailureError(StorageFailureKind.Corrupt));
    }

    [Fact]
    [Trait("Req", "T-03.8")]
    public async Task A_dataset_from_a_newer_app_exits_with_the_newer_schema_reason()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new UnsupportedSchemaError(9, 7)));
        using var coordinator = Create();

        await coordinator.RunAsync();

        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.OpenFailed);
        coordinator.Exit.Should().Be(StartupExit.NewerSchema);
    }

    [Fact]
    public async Task An_unexpected_exception_while_opening_is_shown_as_a_startup_failure_not_thrown()
    {
        _storage.Opens.Enqueue(() => throw new InvalidOperationException("defect"));
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls[^1].Should().Be("shell.failed");
        coordinator.Recovery.Should().BeNull();
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task Retry_after_a_recovery_opens_again_without_first_run_creation()
    {
        _hasPriorUse = false;
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Busy)));
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        using var coordinator = Create();
        await coordinator.RunAsync();

        await coordinator.RetryOpenAsync();

        _log.Calls.Should().NotContain("storage.create", "only the first launch of an unused folder creates a dataset");
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.DatabaseMissing);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task An_interrupted_restore_found_at_startup_shows_recovery_without_opening_the_dataset()
    {
        _storage.HasMarker = true;
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal("shell.starting", "storage.marker?", "shell.recovery:InterruptedRestore");
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Theory]
    [InlineData(InterruptedRestoreOutcome.RolledForward, RecoveryRestoreResult.Completed, DataNotice.InterruptedRestoreFinished)]
    [InlineData(InterruptedRestoreOutcome.RolledBack, RecoveryRestoreResult.RolledBack, DataNotice.InterruptedRestoreUndone)]
    public async Task An_interrupted_restore_is_recovered_then_the_dataset_opens_and_the_outcome_is_reported(
        InterruptedRestoreOutcome recovery, RecoveryRestoreResult expected, DataNotice notice)
    {
        _storage.HasMarker = true;
        _storage.Opens.Enqueue(() => _storage.Opened("recovered"));
        _restores.Recovery = recovery;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.FinishInterruptedRestoreAsync();

        result.Should().Be(expected);
        _log.Calls.Skip(3).Should().Equal(
            "restore.recover-interrupted", "shell.starting", "storage.marker?", "storage.open", "session.create:recovered", "session.attach:recovered", "session.start:recovered", $"shell.notice:{notice}");
        coordinator.Recovery.Should().BeNull();
        coordinator.Exit.Should().Be(StartupExit.Ok);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task An_unresolved_interrupted_restore_keeps_the_recovery_and_opens_nothing()
    {
        _storage.HasMarker = true;
        _restores.Recovery = InterruptedRestoreOutcome.Unresolved;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.FinishInterruptedRestoreAsync();

        result.Should().Be(RecoveryRestoreResult.Unresolved);
        _log.Calls.Skip(3).Should().Equal("restore.recover-interrupted");
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.InterruptedRestore);
    }

    [Fact]
    public async Task Creating_a_dataset_from_the_recovery_page_opens_it()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => _storage.Opened("created"));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.CreateNewDatasetAsync();

        result.Should().Be(CreateDatasetResult.Created);
        _sessions.Current.IsStarted.Should().BeTrue();
        coordinator.Recovery.Should().BeNull();
    }

    [Fact]
    public async Task Creating_a_dataset_is_refused_when_files_appeared_and_nothing_is_overwritten()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => new DatasetOpenAttempt(DatasetOpenOutcome.AlreadyExists, null, null));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.CreateNewDatasetAsync();

        result.Should().Be(CreateDatasetResult.AlreadyExists);
        _sessions.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Declining_the_folder_prompt_cancels_the_restore_without_touching_any_file()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _shell.PickedFolder = null;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Cancelled);
        _log.Calls.Skip(4).Should().Equal("shell.pick-folder");
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.OpenFailed);
    }

    [Fact]
    public async Task Restore_from_the_recovery_page_validates_keeps_a_raw_copy_swaps_then_opens()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _storage.Opens.Enqueue(() => _storage.Opened("restored"));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Completed);
        _log.Calls.Skip(4).Should().Equal(
            "shell.pick-folder", @"restore.validate:D:\Backups\20261101T080000Z-manual", "restore.raw-safety-copy", "restore.swap",
            "shell.starting", "storage.marker?", "storage.open", "session.create:restored", "session.attach:restored", "session.start:restored");
        _restores.RawSafetyCopyTime.Should().Be(Now);
    }

    [Fact]
    public async Task A_backup_that_fails_validation_is_discarded_and_nothing_is_replaced()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _restores.Staged = new FakeStagedRestore(isValid: false);
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Invalid);
        _log.Calls.Skip(4).Should().Equal("shell.pick-folder", @"restore.validate:D:\Backups\20261101T080000Z-manual", "restore.discard");
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.OpenFailed, "the recovery page stays");
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task A_swap_failing_part_way_from_the_recovery_page_reopens_into_the_interrupted_restore_recovery()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _restores.SwapBehavior = () =>
        {
            _storage.HasMarker = true;
            throw new IOException("disk removed");
        };
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Failed);
        _log.Calls.Skip(7).Should().Equal("restore.swap", "shell.starting", "storage.marker?", "shell.recovery:InterruptedRestore");
    }

    [Fact]
    public async Task A_swap_rolled_back_from_the_recovery_page_reports_failure_and_reopens_the_previous_data()
    {
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _storage.Opens.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.Corrupt)));
        _restores.SwapBehavior = () => false;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Failed);
        _log.Calls.Skip(7).Should().Equal("restore.swap", "shell.starting", "storage.marker?", "storage.open", "shell.recovery:OpenFailed");
    }

    [Fact]
    public async Task Settings_restore_takes_a_safety_backup_closes_the_session_swaps_and_reopens()
    {
        var staged = new FakeStagedRestore(isValid: true);
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(() => _storage.Opened("after"));
        using var coordinator = Create();
        await coordinator.RunAsync();
        var before = _sessions.Current;

        var result = await before.Hooks.Restore(staged);

        result.Should().Be(RestoreRunResult.Restored);
        _log.Calls.Skip(6).Should().Equal(
            "backups.create:RestoreSafety", "shell.begin-replacement", "shell.detach", "session.dispose:before", "dataset.dispose:before",
            "restore.swap", "shell.starting", "storage.marker?", "storage.open", "session.create:after", "session.attach:after", "session.start:after", "shell.notice:RestoreDone");
        before.FakeBackups.Created.Should().Be((BackupKind.RestoreSafety, Now));
        _restores.Swapped.Should().BeSameAs(staged);
        _shell.Notices.Should().Equal((DataNotice.RestoreDone, @"C:\data\backups\20261111T090000Z-restore-safety"));
    }

    [Fact]
    public async Task Settings_restore_waits_for_a_running_daily_backup_before_closing_the_session()
    {
        var daily = new TaskCompletionSource<DailyBackupReport>();
        _sessions.NextBackups.Daily = _ => daily.Task;
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(() => _storage.Opened("after"));
        using var coordinator = Create();
        await coordinator.RunAsync();
        var before = _sessions.Current;
        before.BecomeReady();

        var restore = before.Hooks.Restore(new FakeStagedRestore(isValid: true));

        // The close starts right after the shell blocks edits; nothing can close the session until the backup ends.
        await _shell.ReplacementBegun.Task.WithTimeout();
        before.IsDisposed.Should().BeFalse("the daily backup still reads the dataset");

        daily.SetResult(new DailyBackupReport(false));
        (await restore.WithTimeout()).Should().Be(RestoreRunResult.Restored);
        before.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Settings_restore_is_refused_when_the_safety_backup_fails_and_the_data_stays_open()
    {
        _sessions.NextBackups.CreateResult = AppResult.Failure<string>(new StorageFailureError(StorageFailureKind.DiskFull));
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await _sessions.Current.Hooks.Restore(new FakeStagedRestore(isValid: true));

        result.Should().Be(RestoreRunResult.NotStarted);
        _log.Calls.Skip(6).Should().Equal("backups.create:RestoreSafety");
        _sessions.Current.IsDisposed.Should().BeFalse();
        _shell.Notices.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public async Task A_settings_swap_failing_part_way_keeps_the_marker_and_reopens_into_recovery()
    {
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _restores.SwapBehavior = () =>
        {
            _storage.HasMarker = true;
            throw new UnauthorizedAccessException("locked");
        };
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await _sessions.Current.Hooks.Restore(new FakeStagedRestore(isValid: true));

        result.Should().Be(RestoreRunResult.RolledBack);
        _log.Calls.Skip(11).Should().Equal("restore.swap", "shell.starting", "storage.marker?", "shell.recovery:InterruptedRestore");
        _shell.Notices.Should().BeEmpty();
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.InterruptedRestore);
    }

    [Fact]
    public async Task A_settings_swap_that_rolled_back_reopens_the_previous_data_and_reports_an_error_notice()
    {
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(() => _storage.Opened("previous"));
        _restores.SwapBehavior = () => false;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await _sessions.Current.Hooks.Restore(new FakeStagedRestore(isValid: true));

        result.Should().Be(RestoreRunResult.RolledBack);
        _sessions.Current.Dataset.Name.Should().Be("previous");
        _shell.Notices.Should().Equal((DataNotice.RestoreRolledBack, @"C:\data\backups\20261111T090000Z-restore-safety"));
    }

    [Fact]
    public async Task Settings_restore_without_an_open_session_does_nothing()
    {
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        using var coordinator = Create();
        await coordinator.RunAsync();
        var hooks = _sessions.Current.Hooks;
        await hooks.Restore(new FakeStagedRestore(isValid: true));
        var calls = _log.Calls.Count;

        var result = await hooks.Restore(new FakeStagedRestore(isValid: true));

        result.Should().Be(RestoreRunResult.NotStarted);
        _log.Calls.Should().HaveCount(calls);
    }

    [Fact]
    public async Task A_failed_daily_backup_is_reported_in_the_shell_and_startup_continues()
    {
        _sessions.NextBackups.Daily = _ => Task.FromResult(new DailyBackupReport(true, StorageFailureKind.DiskFull));
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();
        await coordinator.RunAsync();

        _sessions.Current.BecomeReady();

        _shell.BackupFailures.Should().Equal(StorageFailureKind.DiskFull);
        coordinator.Exit.Should().Be(StartupExit.Ok);
    }

    [Fact]
    public async Task A_daily_backup_defect_is_reported_as_a_missing_backup_without_a_storage_kind()
    {
        _sessions.NextBackups.Daily = _ => Task.FromException<DailyBackupReport>(new InvalidOperationException("defect"));
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();
        await coordinator.RunAsync();

        _sessions.Current.BecomeReady();

        _shell.BackupFailures.Should().Equal((StorageFailureKind?)null);
    }

    [Fact]
    [Trait("Req", "T-06.6")]
    public async Task Shutdown_cancels_the_daily_backup_then_detaches_the_pages_and_closes_the_session()
    {
        _sessions.NextBackups.Daily = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new DailyBackupReport(false);
        };
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();
        await coordinator.RunAsync();
        _sessions.Current.BecomeReady();

        await coordinator.ShutdownAsync();

        _log.Calls.Skip(7).Should().Equal("shell.begin-shutdown", "shell.detach", "session.dispose:dataset", "dataset.dispose:dataset");
        _shell.BackupFailures.Should().BeEmpty("a cancelled backup is not a failure");
    }

    [Fact]
    public async Task Shutdown_from_the_recovery_page_closes_nothing_and_keeps_the_recovery_exit_reason()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        using var coordinator = Create();
        await coordinator.RunAsync();

        await coordinator.ShutdownAsync();

        _log.Calls.Skip(4).Should().Equal("shell.begin-shutdown", "shell.detach");
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task A_failure_shown_after_the_first_ready_view_does_not_make_the_exit_a_startup_failure()
    {
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();
        await coordinator.RunAsync();
        _sessions.Current.BecomeReady();

        _shell.HasFailed = true;

        coordinator.Exit.Should().Be(StartupExit.Ok);
    }

    [Fact]
    [Trait("Req", "T-06.6")]
    public async Task A_session_that_fails_while_attaching_is_still_closed_by_shutdown()
    {
        _sessions.AttachFailure = new InvalidOperationException("page defect");
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls.Should().Equal("shell.starting", "storage.marker?", "storage.open", "session.create:dataset", "session.attach:dataset", "shell.failed");
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);

        var shutdown = () => coordinator.ShutdownAsync();

        await shutdown.Should().NotThrowAsync();
        _log.Calls.Skip(6).Should().Equal("shell.begin-shutdown", "shell.detach", "session.dispose:dataset", "dataset.dispose:dataset");
    }

    [Fact]
    public async Task A_session_factory_that_throws_shows_the_failure_and_shutdown_still_completes()
    {
        _sessions.CreateFailure = new InvalidOperationException("container defect");
        _storage.Opens.Enqueue(() => _storage.Opened());
        using var coordinator = Create();

        await coordinator.RunAsync();

        _log.Calls[^1].Should().Be("shell.failed");
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);

        var shutdown = () => coordinator.ShutdownAsync();

        await shutdown.Should().NotThrowAsync();
        _log.Calls.Skip(5).Should().Equal(
            "shell.begin-shutdown", "shell.detach");

        // Current behaviour, recorded as a known leak: the opened dataset is never closed when the factory throws.
        _log.Calls.Should().NotContain("dataset.dispose:dataset");
    }

    [Fact]
    public async Task Creating_a_dataset_from_the_recovery_page_that_fails_keeps_the_recovery_page()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => FakeDatasetStorage.Failed(new StorageFailureError(StorageFailureKind.DiskFull)));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.CreateNewDatasetAsync();

        result.Should().Be(CreateDatasetResult.Failed);
        _log.Calls.Skip(4).Should().Equal("storage.create");
        _sessions.Sessions.Should().BeEmpty();
        coordinator.Recovery!.Kind.Should().Be(StartupRecoveryKind.DatabaseMissing);
    }

    [Fact]
    public async Task Creating_a_dataset_that_throws_is_shown_as_a_startup_failure()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Creates.Enqueue(() => throw new InvalidOperationException("defect"));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.CreateNewDatasetAsync();

        result.Should().Be(CreateDatasetResult.Failed);
        _log.Calls.Skip(4).Should().Equal("storage.create", "shell.failed");
        coordinator.Recovery.Should().BeNull();
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task Restore_from_the_recovery_page_continues_when_there_was_no_database_file_to_keep()
    {
        _storage.Opens.Enqueue(FakeDatasetStorage.Missing);
        _storage.Opens.Enqueue(() => _storage.Opened("restored"));
        _restores.RawSafetyCopy = null;
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await coordinator.RestoreFromBackupAsync();

        result.Should().Be(RecoveryRestoreResult.Completed);
        _log.Calls.Skip(6).Should().Equal(
            "restore.raw-safety-copy", "restore.swap", "shell.starting", "storage.marker?", "storage.open",
            "session.create:restored", "session.attach:restored", "session.start:restored");
    }

    [Fact]
    public async Task An_unexpected_exception_reopening_after_a_settings_restore_shows_the_failure_and_still_reports_the_restore()
    {
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(() => throw new InvalidOperationException("defect"));
        using var coordinator = Create();
        await coordinator.RunAsync();

        var result = await _sessions.Current.Hooks.Restore(new FakeStagedRestore(isValid: true));

        result.Should().Be(RestoreRunResult.Restored);
        _log.Calls.Skip(11).Should().Equal("restore.swap", "shell.starting", "storage.marker?", "storage.open", "shell.failed", "shell.notice:RestoreDone");
        coordinator.Exit.Should().Be(StartupExit.StartupFailure);
    }

    [Fact]
    public async Task Every_shell_call_of_an_open_and_a_settings_restore_arrives_on_the_ui_thread()
    {
        _sessions.NextBackups.Daily = _ => Task.Run(() => new DailyBackupReport(true, StorageFailureKind.Io));
        _storage.Opens.Enqueue(() => _storage.Opened("before"));
        _storage.Opens.Enqueue(() => _storage.Opened("after"));
        using var coordinator = Create();

        var uiThread = await SingleThreadSynchronizationContext.RunAsync(async () =>
        {
            await coordinator.RunAsync();
            _sessions.Current.BecomeReady();
            var restored = await _sessions.Current.Hooks.Restore(new FakeStagedRestore(isValid: true));
            restored.Should().Be(RestoreRunResult.Restored);
            await coordinator.ShutdownAsync();
            return Environment.CurrentManagedThreadId;
        }).WithTimeout();

        _log.Calls.Should().Contain(["shell.backup-failed", "shell.begin-replacement", "shell.notice:RestoreDone", "shell.begin-shutdown"]);
        _shell.CallThreads.Should().NotBeEmpty().And.OnlyContain(thread => thread == uiThread);
    }

    private StartupCoordinator Create()
    {
        var time = new FakeTimeProvider(Now);
        var environment = new StartupEnvironment(_hasPriorUse, "9.9.9-test", () => 42);
        return new StartupCoordinator(_storage, _restores, _sessions, _shell, time, environment, NullLogger<StartupCoordinator>.Instance);
    }
}
