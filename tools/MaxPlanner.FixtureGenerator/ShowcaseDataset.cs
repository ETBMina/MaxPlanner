using MaxPlanner.Application.Reviews;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Persistence;

namespace MaxPlanner.FixtureGenerator;

/// <summary>
/// A lively, fictional team workspace for README screenshots. CR planned dates are relative to <paramref name="today"/>
/// so the Calendar's current month shows several bars over different weeks, one CR is overdue, and two wait in the
/// backlog. Tasks cover every Board status; checklists, General to-dos, Subtask cards and reviews fill the other pages.
/// </summary>
internal static class ShowcaseDataset
{
    public static async Task CreateAsync(SqliteDataset dataset, DateOnly today)
    {
        var b = new FixtureBuilder(dataset.Coordinator);
        var workflow = (await dataset.Snapshots.LoadAsync(CancellationToken.None)).Value.Workflow;
        var status = workflow.TaskStatuses.ToDictionary(s => s.Name.Display, s => s.Id);
        var crStatus = workflow.CrStatuses.ToDictionary(s => s.Name.Display, s => s.Id);
        var priority = workflow.Priorities.ToDictionary(p => p.Name.Display, p => p.Id);
        var squad = workflow.Squads.ToDictionary(s => s.Name.Display, s => s.Id);
        var finance = squad["Retail Finance"];
        var cards = squad["Retail Cards"];

        var nora = await b.PersonAsync("Nora Lindqvist");
        var omar = await b.PersonAsync("Omar Farouk");
        var priya = await b.PersonAsync("Priya Raman");
        var lucas = await b.PersonAsync("Lucas Moreau");
        var hana = await b.PersonAsync("Hana Sato");
        var diego = await b.PersonAsync("Diego Alvarez");

        var ux = await b.TagAsync("UX");
        var security = await b.TagAsync("Security");
        var quickWin = await b.TagAsync("Quick win");
        var techDebt = await b.TagAsync("Tech debt");

        var month = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var inProgress = crStatus["In Progress"];

        async Task<ChangeRequestId> Cr(string number, string title, PersonId owner, SquadId squadId, string prio, CrStatusId crStatusId, DateOnly? start, DateOnly? end, string description) =>
            await b.CrAsync(new CrSpec(number, title, owner, squadId, null, priority[prio], crStatusId, start, end, description));

        // Five bars in the current month over different weeks, plus one that runs into next month.
        var wallet = await Cr("CR-2101", "Digital wallet provisioning", nora, cards, "High", inProgress, month.AddDays(1), month.AddDays(10),
            "Let customers add their card to mobile wallets straight from the app.");
        var statements = await Cr("CR-2104", "Redesigned monthly statements", priya, finance, "Medium", inProgress, month.AddDays(5), month.AddDays(17),
            "Clearer statement layout with spending categories and a PDF download.");
        var disputes = await Cr("CR-2109", "Self-service card disputes", omar, cards, "Urgent", inProgress, month.AddDays(9), month.AddDays(14),
            "Customers raise and track a transaction dispute without calling support.");
        var budgets = await Cr("CR-2112", "Savings goals and budgets", lucas, finance, "Medium", inProgress, month.AddDays(15), month.AddDays(26),
            "Set monthly budgets per category and track savings goals.");
        var travel = await Cr("CR-2115", "Travel notice for cards", hana, cards, "Low", inProgress, month.AddDays(20), monthEnd.AddDays(9),
            "Tell the bank about upcoming trips so foreign transactions are not declined.");
        var overdue = await Cr("CR-2098", "Card PIN reset flow", diego, cards, "High", inProgress, today.AddDays(-16), today.AddDays(-3),
            "Reset a forgotten PIN in the app with step-up verification.");
        var loyalty = await Cr("CR-2120", "Loyalty points dashboard", priya, finance, "Medium", crStatus["Backlog"], null, null,
            "Show points balance, history and redemption options.");
        var exports = await Cr("CR-2123", "Bulk transaction export", lucas, finance, "Low", crStatus["Backlog"], null, null,
            "Export a date range of transactions as CSV for accounting tools.");

        async Task<TaskId> AddTask(string? title, string service, ChangeRequestId? cr, string prio, string target, PersonId? designer, PersonId? developer,
            IReadOnlyList<TagId>? tags = null, IReadOnlyList<(string, bool)>? checklist = null)
        {
            var id = await b.TaskAsync(new TaskSpec(title, service, cr, priority[prio], null, designer, developer, tags, null, checklist));
            if (target == "Completed")
            {
                await b.MoveAsync(id, status["In Progress"]);
            }

            if (target != "Backlog")
            {
                await b.MoveAsync(id, status[target]);
            }

            return id;
        }

        var walletApi = await AddTask(null, "Cards API", wallet, "High", "Developing", null, omar, [security],
            [("Agree token format with the network", true), ("Implement provisioning call", true), ("Add retry on timeout", false)]);
        var walletApp = await AddTask(null, "Mobile App", wallet, "High", "Designing", nora, null, [ux]);
        await AddTask(null, "Mobile App", statements, "Medium", "Review", priya, lucas, [ux],
            [("Category breakdown chart", true), ("Accessible table for screen readers", true), ("Dark theme pass", false)]);
        var statementPdf = await AddTask(null, "Documents", statements, "Medium", "Unit Testing", null, diego);
        var disputeForm = await AddTask(null, "Mobile App", disputes, "Urgent", "In Progress", hana, omar, [ux],
            [("Reason picker", true), ("Upload receipt", false), ("Status timeline", false)]);
        await AddTask(null, "Cards API", disputes, "Urgent", "Blocked", null, diego, [security]);
        await AddTask(null, "Payments", budgets, "Medium", "Backlog", null, lucas);
        await AddTask(null, "Mobile App", budgets, "Medium", "Designing", priya, null, [ux]);
        await AddTask(null, "Mobile App", travel, "Low", "Backlog", hana, null);
        var pinApi = await AddTask(null, "Cards API", overdue, "High", "Review", null, omar, [security],
            [("Step-up verification", true), ("Rate limiting", true), ("Pen test sign-off", false)]);
        await AddTask(null, "Mobile App", overdue, "High", "Completed", nora, diego);
        await AddTask(null, "Analytics", loyalty, "Low", "Backlog", priya, null);
        await AddTask("Upgrade logging library", "Platform", null, "Low", "Completed", null, lucas, [techDebt]);
        await AddTask("Fix login timeout on slow networks", "Mobile App", null, "High", "Developing", null, hana, [quickWin],
            [("Reproduce on throttled network", true), ("Raise the timeout", false)]);
        await AddTask("Rotate API signing keys", "Platform", null, "Medium", "Blocked", null, omar, [security]);
        await AddTask("Clean up feature flags", "Platform", null, "Low", "Unit Testing", null, diego, [techDebt]);
        await AddTask(null, "Web Portal", exports, "Low", "In Progress", nora, lucas);

        await b.CardAsync(walletApi, "Network sandbox certificates", status: status["Completed"]);
        await b.CardAsync(walletApp, "Wallet icon assets", status: status["Designing"]);
        await b.CardAsync(disputeForm, "Receipt upload size limits", status: status["In Progress"]);
        await b.CardAsync(statementPdf, "Font embedding check", status: status["Review"]);
        await b.CardAsync(pinApi, "Lockout copy review", status: status["Blocked"]);

        await b.GeneralTodoAsync("Prepare sprint demo", false);
        await b.GeneralTodoAsync("Book the quarterly planning room", true);
        await b.GeneralTodoAsync("Update the on-call rota", false);
        await b.GeneralTodoAsync("Share release notes with support", false);

        await b.ReviewAsync(new ReviewDraft("Tokenisation endpoint", "CR-2101", null, ["Cards API"], omar, "Check error mapping for declined tokens."), false);
        await b.ReviewAsync(new ReviewDraft("Statement layout", "CR-2104", null, ["Mobile App"], lucas, null), false);
        await b.ReviewAsync(new ReviewDraft("PIN reset service", "CR-2098", null, ["Cards API"], omar, "Security review requested."), false);
        await b.ReviewAsync(new ReviewDraft("Clean up feature flags", null, null, ["Platform"], diego, null), false);
        await b.ReviewAsync(new ReviewDraft("PIN reset screens", "CR-2098", null, ["Mobile App"], diego, "Looks good."), true);
        await b.ReviewAsync(new ReviewDraft("Upgrade logging library", null, null, ["Platform"], lucas, null), true);
    }
}
