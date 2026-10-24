using System.Globalization;
using MaxPlanner.Infrastructure.Persistence;

namespace MaxPlanner.FixtureGenerator;

/// <summary>The small demonstration dataset: two CRs, linked and unlinked tasks, checklist items, and Subtask cards.</summary>
internal static class DemoDataset
{
    public static async Task CreateAsync(SqliteDataset dataset, int extraTasks)
    {
        var builder = new FixtureBuilder(dataset.Coordinator);
        var alice = await builder.PersonAsync("Alice Haddad");
        var bob = await builder.PersonAsync("Bob Mansour");
        var workflow = (await dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var status = workflow.TaskStatuses.ToDictionary(s => s.Name.Display, s => s.Id);
        var priority = workflow.Priorities.ToDictionary(p => p.Name.Display, p => p.Id);
        var squad = workflow.Squads.ToDictionary(s => s.Name.Display, s => s.Id);

        var freeze = await builder.CrAsync(new CrSpec("CR-1042", "Instant card freeze", alice, squad["Retail Cards"], "00457"));
        var limits = await builder.CrAsync(new CrSpec("CR-1067", "Card limits API", bob, squad["Retail Finance"], "00512"));

        var onboarding = await builder.TaskAsync(new TaskSpec("Update onboarding copy", "Mobile Banking", null, priority["Medium"]));
        await builder.ChecklistAsync(onboarding, ("Draft the new welcome text", true), ("Review with legal", false), ("Hand over to translation", false));
        await builder.CardAsync(onboarding, "Translate onboarding strings");

        var freezeApi = await builder.TaskAsync(new TaskSpec(null, "Cards API", freeze, priority["High"]));
        await builder.MoveAsync(freezeApi, status["In Progress"]);
        await builder.ChecklistAsync(freezeApi, ("Define the freeze endpoint", true), ("Add audit logging", false));

        var freezeApp = await builder.TaskAsync(new TaskSpec(null, "Mobile App", freeze, priority["Urgent"]));
        await builder.MoveAsync(freezeApp, status["Review"]);

        var limitsService = await builder.TaskAsync(new TaskSpec(null, "Payments", limits, priority["Medium"]));
        await builder.MoveAsync(limitsService, status["Completed"]);
        var rounding = await builder.CardAsync(limitsService, "Fix rounding in daily limits");
        await builder.MoveCardAsync(rounding, status["In Progress"]);
        await builder.CardAsync(limitsService, "Update the limit error copy");

        var certificate = await builder.TaskAsync(new TaskSpec("Renew the TLS certificate", "Platform", null, priority["Low"]));
        await builder.MoveAsync(certificate, status["Blocked"]);
        await builder.ChecklistAsync(certificate, ("Request the certificate", false));

        for (var i = 1; i <= extraTasks; i++)
        {
            await builder.TaskAsync(new TaskSpec(string.Create(CultureInfo.InvariantCulture, $"Extra task {i:D5}"), "Fixture", null, priority["Medium"]));
        }
    }
}
