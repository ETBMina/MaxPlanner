using System.ComponentModel;
using AwesomeAssertions;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Tests.Shell;

public sealed class ShellViewModelTests
{
    [Fact]
    [Trait("Req", "T-09")]
    public void A_new_shell_starts_in_the_starting_state()
    {
        var shell = new ShellViewModel();

        shell.State.Should().Be(ShellState.Starting);
        shell.IsStarting.Should().BeTrue();
        shell.FailureMessage.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "T-09")]
    public void A_startup_failure_keeps_an_explanation_and_notifies_every_derived_state()
    {
        var shell = new ShellViewModel();
        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        shell.MarkFailed("The database could not be opened.");

        shell.HasFailed.Should().BeTrue();
        shell.IsStarting.Should().BeFalse();
        shell.FailureMessage.Should().Be("The database could not be opened.");
        changed.Should().Contain([nameof(ShellViewModel.FailureMessage), nameof(ShellViewModel.State),
            nameof(ShellViewModel.IsStarting), nameof(ShellViewModel.IsReady), nameof(ShellViewModel.HasFailed)]);
    }

    [Fact]
    [Trait("Req", "T-09")]
    public void Becoming_ready_clears_a_previous_failure()
    {
        var shell = new ShellViewModel();
        shell.MarkFailed("Temporary failure.");

        shell.MarkReady();

        shell.IsReady.Should().BeTrue();
        shell.FailureMessage.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "T-09")]
    public void A_failure_without_a_message_is_rejected()
    {
        var shell = new ShellViewModel();

        var act = () => shell.MarkFailed("  ");

        act.Should().Throw<ArgumentException>();
        shell.State.Should().Be(ShellState.Starting);
    }

    [Fact]
    public void ViewModels_load_in_process_without_a_xaml_runtime()
    {
        // Spike 9: constructing and observing a Presentation ViewModel needs no DispatcherQueue,
        // XAML Application, or WinRT activation, so ViewModel tests run as ordinary unit tests.
        INotifyPropertyChanged shell = new ShellViewModel();

        shell.Should().NotBeNull();
    }
}
