using AwesomeAssertions;
using MaxPlanner.Infrastructure.Storage;

namespace MaxPlanner.Infrastructure.Tests.Storage;

public sealed class DatasetLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst Lock", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Req", "T-06.5")]
    public void Only_one_holder_can_own_a_dataset_at_a_time()
    {
        var paths = DataPaths.ForDirectory(_root);

        using var first = DatasetLock.TryAcquire(paths);
        var second = DatasetLock.TryAcquire(paths);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "T-06.5")]
    public void Releasing_the_lock_lets_the_next_launch_open_the_dataset()
    {
        var paths = DataPaths.ForDirectory(_root);
        DatasetLock.TryAcquire(paths)!.Dispose();

        using var reopened = DatasetLock.TryAcquire(paths);

        reopened.Should().NotBeNull();
    }

    [Fact]
    [Trait("Req", "T-06.5")]
    public void Separate_portable_copies_lock_independently()
    {
        using var copyA = DatasetLock.TryAcquire(DataPaths.ForDirectory(Path.Combine(_root, "A")));
        using var copyB = DatasetLock.TryAcquire(DataPaths.ForDirectory(Path.Combine(_root, "B")));

        copyA.Should().NotBeNull();
        copyB.Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
