using System.Globalization;
using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Workflow;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.Infrastructure.Tests.Persistence;

/// <summary>A clock whose UTC instant and local time zone the test controls.</summary>
internal sealed class TestClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>
/// A real dataset created through <see cref="SqliteDataset.CreateNewAsync"/> in a unique temp folder whose
/// path contains a space and non-ASCII text, plus shortcuts that run the real Application use cases.
/// </summary>
internal sealed class TestDataset : IAsyncDisposable
{
    private bool _isOpen;

    private TestDataset(string root, SqliteDatasetOptions options, TestClock clock, SqliteDataset dataset)
    {
        Root = root;
        Paths = DataPaths.ForDirectory(root);
        Options = options;
        Clock = clock;
        Dataset = dataset;
        _isOpen = true;
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Root { get; }

    public DataPaths Paths { get; }

    public SqliteDatasetOptions Options { get; set; }

    public TestClock Clock { get; }

    public SqliteDataset Dataset { get; private set; }

    public SqliteMutationCoordinator Coordinator => Dataset.Coordinator;

    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "MaxPlanner Tëst DB", Guid.NewGuid().ToString("N"));

    public static async Task<TestDataset> CreateAsync()
    {
        var root = NewRoot();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
        var options = new SqliteDatasetOptions(clock);
        var result = await SqliteDataset.CreateNewAsync(DataPaths.ForDirectory(root), options);
        result.Error.Should().BeNull();
        result.Status.Should().Be(DatasetOpenStatus.Opened);
        return new TestDataset(root, options, clock, result.Dataset!);
    }

    public async Task CloseAsync()
    {
        if (_isOpen)
        {
            _isOpen = false;
            await Dataset.DisposeAsync();
        }
    }

    /// <summary>Closes the dataset (draining, checkpointing) and opens the file again, as a restart does.</summary>
    public async Task ReopenAsync()
    {
        await CloseAsync();
        var result = await SqliteDataset.OpenExistingAsync(Paths, Options);
        result.Error.Should().BeNull();
        result.Status.Should().Be(DatasetOpenStatus.Opened);
        Dataset = result.Dataset!;
        _isOpen = true;
    }

    public async Task<ReadSnapshot> SnapshotAsync()
    {
        var result = await Dataset.Snapshots.LoadAsync(Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    public async Task<TaskStatusDefinition> TaskStatusAsync(string name) =>
        (await SnapshotAsync()).Workflow.TaskStatuses.Single(s => s.Name.Display == name);

    /// <summary>A separate, unpooled connection for arranging rows and asserting on raw storage.</summary>
    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={Paths.DatabaseFile};Pooling=False");
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = Command(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public long Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenRaw();
        using var command = Command(connection, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public PersonId AddPerson(string name)
    {
        var id = Guid.NewGuid();
        Execute("INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);", ("$id", id), ("$name", name), ("$norm", name.ToUpperInvariant()));
        return new PersonId(id);
    }

    public TagId AddTag(string name)
    {
        var id = Guid.NewGuid();
        Execute("INSERT INTO tags (id, name, name_norm) VALUES ($id, $name, $norm);", ("$id", id), ("$name", name), ("$norm", name.ToUpperInvariant()));
        return new TagId(id);
    }

    public FieldId AddField(string key, FieldKind kind, string optionsJson = "[]")
    {
        var id = Guid.NewGuid();
        Execute(
            """
            INSERT INTO field_definitions (id, target, field_key, key_norm, label, kind, is_required, default_value_json, display_order, options_json, is_active)
            VALUES ($id, 'Task', $key, $norm, $key, $kind, 0, NULL, 0, $options, 1);
            """,
            ("$id", id),
            ("$key", key),
            ("$norm", key.ToLowerInvariant()),
            ("$kind", kind.ToString()),
            ("$options", optionsJson));
        return new FieldId(id);
    }

    public static TaskContentDraft Draft(string? title = "Card freeze", string? service = null, string? notes = null) =>
        new(title, service, [], [], [], notes, CustomValue.NoValues);

    public async Task<TaskId> AddTaskAsync(string title = "Card freeze", TaskStatusId? status = null, Guid? id = null, string? service = null)
    {
        var taskId = new TaskId(id ?? Guid.NewGuid());
        return Succeeded(await new CreateTask(Coordinator).ExecuteAsync(
            new CreateTaskCommand(OperationId.New(), taskId, Draft(title, service), status), Ct)).Value;
    }

    public async Task<ChangeRequestId> AddCrAsync(PersonId owner, string number = "CR-1042", string title = "Instant card freeze")
    {
        var id = new ChangeRequestId(Guid.NewGuid());
        var draft = new ChangeRequestDraft(number, title, owner, null, null, null, null, null, null, null, "0042", CustomValue.NoValues);
        return Succeeded(await new CreateChangeRequest(Coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), id, draft), Ct)).Value;
    }

    public static CommandOutcome<T> Succeeded<T>(AppResult<CommandOutcome<T>> result)
    {
        result.Error.Should().BeNull();
        return result.Value;
    }

    public static TError Failed<T, TError>(AppResult<CommandOutcome<T>> result)
        where TError : AppError
    {
        result.IsSuccess.Should().BeFalse();
        return result.Error.Should().BeOfType<TError>().Subject;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: temp folders are disposable.
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            // Microsoft.Data.Sqlite binds a Guid as TEXT by default; schema v1 stores IDs as 16-byte BLOBs.
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                Guid id => id.ToByteArray(),
                _ => value,
            });
        }

        return command;
    }
}
