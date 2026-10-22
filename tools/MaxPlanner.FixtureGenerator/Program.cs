using System.Globalization;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.ChangeRequests;
using MaxPlanner.Application.Checklists;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.SubtaskCards;
using MaxPlanner.Application.Tasks;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence;
using MaxPlanner.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MaxPlanner.FixtureGenerator;

/// <summary>
/// Creates a small disposable dataset for UI evidence and manual checks: CRs, linked and unlinked tasks,
/// checklist items, and Subtask cards, all through the real Application use cases on a real SQLite file.
/// </summary>
/// <remarks>
/// Usage: <c>MaxPlanner.FixtureGenerator &lt;app-folder&gt; [--extra-tasks N]</c>. The dataset is created at
/// <c>&lt;app-folder&gt;/data/data.db</c>, the layout the portable EXE uses beside itself; the folder must not
/// contain a dataset yet. Never point it at real user data.
/// </remarks>
internal static class Program
{
    private static readonly CancellationToken None = CancellationToken.None;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--extra-tasks"))
        {
            await Console.Error.WriteLineAsync("Usage: MaxPlanner.FixtureGenerator <app-folder> [--extra-tasks N]");
            return 2;
        }

        var extraTasks = args.Length == 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0;
        var paths = DataPaths.ForDirectory(Path.GetFullPath(args[0]));
        var created = await SqliteDataset.CreateNewAsync(paths);
        if (created.Dataset is not { } dataset)
        {
            await Console.Error.WriteLineAsync($"Could not create the dataset: {created.Status} {created.Error?.Code}");
            return 1;
        }

        await using (dataset)
        {
            var alice = AddPerson(paths, "Alice Haddad");
            var bob = AddPerson(paths, "Bob Mansour");
            var snapshot = (await dataset.Snapshots.LoadAsync(None)).Value;
            var workflow = snapshot.Workflow;
            var status = workflow.TaskStatuses.ToDictionary(s => s.Name.Display, s => s.Id);
            var priority = workflow.Priorities.ToDictionary(p => p.Name.Display, p => p.Id);
            var squad = workflow.Squads.ToDictionary(s => s.Name.Display, s => s.Id);
            var builder = new Builder(dataset.Coordinator);

            var freeze = await builder.CrAsync("CR-1042", "Instant card freeze", alice, squad["Retail Cards"], "00457");
            var limits = await builder.CrAsync("CR-1067", "Card limits API", bob, squad["Retail Finance"], "00512");

            var onboarding = await builder.TaskAsync("Update onboarding copy", "Mobile Banking", null, priority["Medium"]);
            await builder.ChecklistAsync(onboarding, ("Draft the new welcome text", true), ("Review with legal", false), ("Hand over to translation", false));
            await builder.CardAsync(onboarding, "Translate onboarding strings", null);

            var freezeApi = await builder.TaskAsync(null, "Cards API", freeze, priority["High"]);
            await builder.MoveAsync(freezeApi, status["In Progress"]);
            await builder.ChecklistAsync(freezeApi, ("Define the freeze endpoint", true), ("Add audit logging", false));

            var freezeApp = await builder.TaskAsync(null, "Mobile App", freeze, priority["Urgent"]);
            await builder.MoveAsync(freezeApp, status["Review"]);

            var limitsService = await builder.TaskAsync(null, "Payments", limits, priority["Medium"]);
            await builder.MoveAsync(limitsService, status["Completed"]);
            var rounding = await builder.CardAsync(limitsService, "Fix rounding in daily limits", null);
            await builder.MoveCardAsync(rounding, status["In Progress"]);
            await builder.CardAsync(limitsService, "Update the limit error copy", null);

            var certificate = await builder.TaskAsync("Renew the TLS certificate", "Platform", null, priority["Low"]);
            await builder.MoveAsync(certificate, status["Blocked"]);
            await builder.ChecklistAsync(certificate, ("Request the certificate", false));

            for (var i = 1; i <= extraTasks; i++)
            {
                await builder.TaskAsync(string.Create(CultureInfo.InvariantCulture, $"Extra task {i:D5}"), "Fixture", null, priority["Medium"]);
            }

            var final = (await dataset.Snapshots.LoadAsync(None)).Value;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Created {paths.DatabaseFile}: {final.ChangeRequests.Count} CRs, {final.Tasks.Count} tasks, {final.SubtaskCards.Count} Subtask cards, {final.Tasks.Sum(t => t.ChecklistItems.Count)} checklist items."));
        }

        SqliteConnection.ClearAllPools();
        return 0;
    }

    /// <summary>People have no use case until Phase 10 (B-11 settings); insert the lookup rows as the tests do.</summary>
    private static PersonId AddPerson(DataPaths paths, string name)
    {
        var id = Guid.NewGuid();
        using var connection = new SqliteConnection($"Data Source={paths.DatabaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; INSERT INTO people (id, name, name_norm) VALUES ($id, $name, $norm);";
        command.Parameters.AddWithValue("$id", id.ToByteArray());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$norm", name.ToUpperInvariant());
        command.ExecuteNonQuery();
        return new PersonId(id);
    }

    /// <summary>Thin wrappers over the use cases that fail loudly, because a fixture must be complete or absent.</summary>
    private sealed class Builder(IMutationCoordinator coordinator)
    {
        private readonly Dictionary<TaskId, long> _taskRevisions = [];
        private readonly Dictionary<SubtaskCardId, long> _cardRevisions = [];
        private readonly Dictionary<ChangeRequestId, long> _crRevisions = [];

        public async Task<ChangeRequestId> CrAsync(string number, string title, PersonId owner, SquadId squad, string taskNumber)
        {
            var id = new ChangeRequestId(Guid.NewGuid());
            var draft = new ChangeRequestDraft(number, title, owner, squad, null, null, null, null, null, null, taskNumber, CustomValue.NoValues);
            Ok(await new CreateChangeRequest(coordinator).ExecuteAsync(new CreateChangeRequestCommand(OperationId.New(), id, draft), None));
            _crRevisions[id] = 1;
            return id;
        }

        public async Task<TaskId> TaskAsync(string? title, string service, ChangeRequestId? cr, PriorityId priority)
        {
            var id = new TaskId(Guid.NewGuid());
            var draft = new TaskContentDraft(title, service, [], [], [], null, CustomValue.NoValues);
            var link = cr is { } crId ? new CrLinkTarget(crId, _crRevisions[crId]) : null;
            Ok(await new CreateTask(coordinator).ExecuteAsync(new CreateTaskCommand(OperationId.New(), id, draft, PriorityId: priority, LinkTo: link), None));
            _taskRevisions[id] = 1;
            return id;
        }

        public async Task MoveAsync(TaskId task, TaskStatusId target) =>
            _taskRevisions[task] = Ok(await new MoveTask(coordinator).ExecuteAsync(
                new MoveTaskCommand(OperationId.New(), task, _taskRevisions[task], target, BoardPlacement.AppendToEnd), None));

        public async Task ChecklistAsync(TaskId task, params (string Text, bool Done)[] items)
        {
            foreach (var (text, done) in items)
            {
                var item = new ChecklistItemId(Guid.NewGuid());
                _taskRevisions[task] = Ok(await new AddChecklistItem(coordinator).ExecuteAsync(
                    new AddChecklistItemCommand(OperationId.New(), task, _taskRevisions[task], item, text), None));
                if (done)
                {
                    _taskRevisions[task] = Ok(await new SetChecklistItemCompleted(coordinator).ExecuteAsync(
                        new SetChecklistItemCompletedCommand(OperationId.New(), task, _taskRevisions[task], item, true), None));
                }
            }
        }

        public async Task<SubtaskCardId> CardAsync(TaskId parent, string subtitle, string? notes)
        {
            var id = new SubtaskCardId(Guid.NewGuid());
            Ok(await new CreateSubtaskCard(coordinator).ExecuteAsync(new CreateSubtaskCardCommand(OperationId.New(), parent, id, subtitle, notes), None));
            _cardRevisions[id] = 1;
            return id;
        }

        public async Task MoveCardAsync(SubtaskCardId card, TaskStatusId target) =>
            _cardRevisions[card] = Ok(await new MoveSubtaskCard(coordinator).ExecuteAsync(
                new MoveSubtaskCardCommand(OperationId.New(), card, _cardRevisions[card], target, BoardPlacement.AppendToEnd), None));

        private static T Ok<T>(AppResult<CommandOutcome<T>> result) =>
            result.IsSuccess ? result.Value.Value : throw new InvalidOperationException($"Fixture command failed: {result.Error}");
    }
}
