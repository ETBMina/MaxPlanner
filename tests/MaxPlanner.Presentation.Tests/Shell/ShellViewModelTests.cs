using System.ComponentModel;
using System.Reactive.Concurrency;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Preferences;
using MaxPlanner.Presentation.Shell;
using MaxPlanner.Presentation.Tests.Testing;
using static MaxPlanner.Presentation.Tests.Testing.Records;

namespace MaxPlanner.Presentation.Tests.Shell;

[Trait("Req", "T-09")]
public sealed class ShellViewModelTests
{
    private readonly FakeDataFolder _dataFolder = new();

    [Fact]
    public void A_new_shell_starts_in_the_starting_state_with_editing_blocked()
    {
        var shell = NewShell();

        shell.State.Should().Be(ShellState.Starting);
        shell.IsStarting.Should().BeTrue();
        shell.IsEditingBlocked.Should().BeTrue();
        shell.FailureMessage.Should().BeNull();
    }

    [Fact]
    public void A_startup_failure_keeps_an_explanation_and_notifies_every_derived_state()
    {
        var shell = NewShell();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.MarkFailed("The database could not be opened.");

        shell.HasFailed.Should().BeTrue();
        shell.CanRetry.Should().BeFalse("an unexpected startup failure has no automatic retry");
        shell.FailureMessage.Should().Be("The database could not be opened.");
        changed.Should().Contain([nameof(ShellViewModel.FailureMessage), nameof(ShellViewModel.State),
            nameof(ShellViewModel.IsStarting), nameof(ShellViewModel.IsReady), nameof(ShellViewModel.IsInRecovery), nameof(ShellViewModel.HasFailed)]);
    }

    [Fact]
    public void A_failure_without_a_message_is_rejected()
    {
        var shell = NewShell();

        var act = () => shell.MarkFailed("  ");

        act.Should().Throw<ArgumentException>();
        shell.State.Should().Be(ShellState.Starting);
    }

    [Fact]
    [Trait("Req", "AC-15")]
    public void The_recovery_page_replaces_the_startup_view()
    {
        var shell = NewShell();
        var recovery = new RecoveryViewModel(Info(RecoveryKind.Corrupt), new FakeStrings(), new FakeRecoveryActions(), _dataFolder);

        shell.ShowRecovery(recovery);

        shell.IsInRecovery.Should().BeTrue();
        shell.Recovery.Should().BeSameAs(recovery);
        shell.IsEditingBlocked.Should().BeTrue();
    }

    [Fact]
    [Trait("Req", "T-07")]
    public void Store_loading_maps_to_starting_and_ready_maps_to_ready()
    {
        using var h = new StoreHarness();
        var shell = NewShell();
        using var attached = shell.AttachStore(h.Store, ImmediateScheduler.Instance);

        shell.State.Should().Be(ShellState.Starting);

        h.Store.Start();
        h.Scheduler.RunAll();
        h.CompleteLoad(Snapshot(3, tasks: [Task("One"), Task("Two")], crs: [Cr()], reviews: [Review()]));

        shell.State.Should().Be(ShellState.Ready);
        shell.IsEditingBlocked.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "T-07")]
    [Trait("Req", "AC-07")]
    public void Resyncing_stays_on_the_ready_view_but_shows_the_notice_and_blocks_editing()
    {
        using var h = new StoreHarness().StartWith(Snapshot(5));
        var shell = NewShell();
        using var attached = shell.AttachStore(h.Store, ImmediateScheduler.Instance);

        h.Publish(Change(7));

        shell.State.Should().Be(ShellState.Ready);
        shell.IsResyncing.Should().BeTrue();
        shell.IsEditingBlocked.Should().BeTrue();

        h.CompleteLoad(Snapshot(7));

        shell.IsResyncing.Should().BeFalse();
        shell.IsEditingBlocked.Should().BeFalse();
    }

    [Fact]
    [Trait("Req", "T-07")]
    public void A_faulted_load_shows_the_failure_and_retry_reloads_the_store()
    {
        using var h = new StoreHarness();
        var strings = new FakeStrings()
            .With("Shell_LoadFailedStorageMessage", "{0} Retry.")
            .With("StorageFailure_Busy", "Busy.");
        var shell = new ShellViewModel(strings, _dataFolder, new FakePreferencesStore());
        using var attached = shell.AttachStore(h.Store, ImmediateScheduler.Instance);
        h.Store.Start();
        h.Scheduler.RunAll();

        h.Snapshots.Fail(new StorageFailureError(StorageFailureKind.Busy));
        h.Scheduler.RunAll();

        shell.State.Should().Be(ShellState.Failed);
        shell.FailureMessage.Should().Be("Busy. Retry.");
        shell.CanRetry.Should().BeTrue();
        shell.RetryCommand.CanExecute(null).Should().BeTrue();

        shell.RetryCommand.Execute(null);
        h.Scheduler.RunAll();

        h.Snapshots.LoadCount.Should().Be(2);
        shell.State.Should().Be(ShellState.Starting);
        h.CompleteLoad(Snapshot(1));
        shell.State.Should().Be(ShellState.Ready);
        shell.CanRetry.Should().BeFalse();
    }

    [Fact]
    public void Shutting_down_blocks_editing_and_stops_following_the_store()
    {
        using var h = new StoreHarness().StartWith(Snapshot(1));
        var shell = NewShell();
        using var attached = shell.AttachStore(h.Store, ImmediateScheduler.Instance);
        shell.IsEditingBlocked.Should().BeFalse();

        shell.BeginShutdown();

        // A sequence gap makes the store resynchronize; the closing shell no longer follows it.
        h.Publish(Change(3));

        h.Status.Should().Be(Presentation.Common.Store.ProjectionStatus.Resyncing);
        shell.IsEditingBlocked.Should().BeTrue();
        shell.IsResyncing.Should().BeFalse();
    }

    [Theory]
    [Trait("Req", "AC-15")]
    [InlineData(PreferencesRecovery.RestoredLastGood, "Shell_PreferencesRestoredMessage")]
    [InlineData(PreferencesRecovery.ResetToDefaults, "Shell_PreferencesResetMessage")]
    public void Reset_preferences_are_reported_in_a_closable_notice(PreferencesRecovery recovery, string expectedKey)
    {
        var shell = NewShell();

        shell.ReportPreferencesRecovery(recovery);

        shell.IsPreferencesNoticeOpen.Should().BeTrue();
        shell.PreferencesNoticeMessage.Should().Be(expectedKey);
        shell.IsPreferencesNoticeOpen = false;
        shell.IsPreferencesNoticeOpen.Should().BeFalse();
    }

    [Fact]
    public void Healthy_preferences_raise_no_notice()
    {
        var shell = NewShell();

        shell.ReportPreferencesRecovery(PreferencesRecovery.None);

        shell.IsPreferencesNoticeOpen.Should().BeFalse();
    }

    [Fact]
    public void A_failed_daily_backup_is_reported_as_a_warning_with_its_cause()
    {
        var strings = new FakeStrings().With("Shell_BackupFailedMessage", "{0} Continue.").With("StorageFailure_DiskFull", "Full.");
        var shell = new ShellViewModel(strings, _dataFolder, new FakePreferencesStore());

        shell.ReportBackupFailed(StorageFailureKind.DiskFull);

        shell.IsBackupWarningOpen.Should().BeTrue();
        shell.BackupWarningMessage.Should().Be("Full. Continue.");
    }

    [Fact]
    public void Open_data_folder_uses_the_launcher()
    {
        var shell = NewShell();

        shell.OpenDataFolderCommand.Execute(null);

        _dataFolder.OpenCount.Should().Be(1);
        shell.DataFolderPath.Should().Be(_dataFolder.DataDirectory);
    }

    [Fact]
    public void ViewModels_load_in_process_without_a_xaml_runtime()
    {
        // Spike 9: constructing and observing a Presentation ViewModel needs no DispatcherQueue,
        // XAML Application, or WinRT activation, so ViewModel tests run as ordinary unit tests.
        INotifyPropertyChanged shell = NewShell();

        shell.Should().NotBeNull();
    }

    private static RecoveryInfo Info(RecoveryKind kind) => new(kind, @"C:\app\data", @"C:\app\data\backups");

    private ShellViewModel NewShell() => new(new FakeStrings(), _dataFolder, new FakePreferencesStore());
}
