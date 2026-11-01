using AwesomeAssertions;
using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Domain.Identifiers;
using static MaxPlanner.Application.Tests.TestWorkspace;

namespace MaxPlanner.Application.Tests.Transfer;

/// <summary>
/// ImportPlanner rules for partial (tabular XLSX/CSV) records, whose <c>SuppliedFields</c> name the columns present:
/// a new CR without a status or title column, and permanent links when a row supplies a blank ChangeRequestId (ADR 0006).
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "T-10")]
public sealed class TabularImportPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private readonly TestWorkspace _workspace = new();

    private static HashSet<string> Columns(params string[] names) => new(names, StringComparer.Ordinal);

    private ReadSnapshot Snapshot()
    {
        var data = _workspace.Data;
        return new ReadSnapshot(data.CommitSequence, [.. data.Tasks.Values], [.. data.SubtaskCards.Values], [.. data.ChangeRequests.Values], [.. data.Links.Values],
            [.. data.Reviews.Values], [.. data.GeneralTodos.Values], data.Workflow);
    }

    private ImportPlan Plan(TransferDocument document, ImportMode mode = ImportMode.Merge) =>
        ImportPlanner.Plan(document, Snapshot(), mode, ImportContext.Empty, Now);

    [Fact]
    public void A_new_cr_without_a_status_column_gets_the_default_cr_status()
    {
        var id = Guid.NewGuid();
        var document = new TransferDocument
        {
            ChangeRequests = [new TransferChangeRequest { Id = id, Number = "CR-77", Title = "Partial", OwnerId = Alice.Id.Value, SuppliedFields = Columns("Id", "Number", "Title", "OwnerId") }],
        };

        var plan = Plan(document, ImportMode.CreateOnly);

        plan.HasErrors.Should().BeFalse();
        plan.ChangeRequests.Should().ContainSingle().Which.Record.StatusId.Should().Be(SeedWorkflow.Defaults.DefaultCrStatusId);
    }

    [Fact]
    public async Task A_merged_cr_without_a_status_column_keeps_its_status()
    {
        var cr = await _workspace.AddCrAsync();
        var status = _workspace.Cr(cr).StatusId;
        var document = new TransferDocument
        {
            ChangeRequests = [new TransferChangeRequest { Id = cr.Value, Title = "Retitled", SuppliedFields = Columns("Id", "Title") }],
        };

        var plan = Plan(document);

        var planned = plan.ChangeRequests.Should().ContainSingle().Subject;
        (planned.Record.Title.Value, planned.Record.StatusId, planned.IsCreate).Should().Be(("Retitled", status, false));
    }

    [Fact]
    public void A_new_cr_without_a_title_column_is_reported_instead_of_dropped_silently()
    {
        var id = Guid.NewGuid();
        var document = new TransferDocument
        {
            ChangeRequests = [new TransferChangeRequest { Id = id, Number = "CR-78", OwnerId = Alice.Id.Value, SuppliedFields = Columns("Id", "Number", "OwnerId") }],
        };

        var plan = Plan(document, ImportMode.CreateOnly);

        plan.ChangeRequests.Should().BeEmpty();
        plan.Findings.Should().Contain(f => f.Severity == ImportSeverity.Error && f.RecordKey == id.ToString() && f.Field == "Title");
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_tabular_row_with_a_blank_change_request_id_for_a_linked_task_is_an_unlink_conflict_and_applies_nothing()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, Notes = "Changed with the unlink", SuppliedFields = Columns("Id", TransferColumns.ChangeRequestId, "Notes") }],
        };

        var plan = Plan(document);

        plan.Findings.Should().ContainSingle().Which.Should().Be(
            new ImportFinding(ImportSeverity.Conflict, ImportEntity.Task, task.Value.ToString(), ImportFindingCodes.UnlinkRefused, "ChangeRequestId"));
        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 0, 0, 1, 0));
        (plan.Tasks.Count, plan.Links.Count).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_merged_cr_without_a_title_column_keeps_its_title()
    {
        var cr = await _workspace.AddCrAsync();
        var title = _workspace.Cr(cr).Title;
        var document = new TransferDocument
        {
            ChangeRequests = [new TransferChangeRequest { Id = cr.Value, EmailSubject = "Re: freeze", SuppliedFields = Columns("Id", "EmailSubject") }],
        };

        var plan = Plan(document);

        plan.Findings.Should().BeEmpty();
        var planned = plan.ChangeRequests.Should().ContainSingle().Subject;
        (planned.Record.Title, planned.Record.EmailSubject?.Value).Should().Be((title, "Re: freeze"));
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task In_create_only_a_blank_change_request_id_on_a_linked_task_is_skipped_as_existing_without_a_conflict()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, Notes = "Ignored", SuppliedFields = Columns("Id", TransferColumns.ChangeRequestId, "Notes") }],
        };

        var plan = Plan(document, ImportMode.CreateOnly);

        plan.Findings.Should().ContainSingle().Which.Should().Be(new ImportFinding(ImportSeverity.Info, ImportEntity.Task, task.Value.ToString(), ImportFindingCodes.Exists));
        plan.Counts[ImportEntity.Task].Should().Be(new ImportEntityCounts(0, 0, 1, 0, 0));
        plan.Tasks.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_checklist_only_row_replaces_the_checklist_of_a_linked_task_without_a_conflict()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        var item = new TransferChecklistItem(Guid.NewGuid(), "From the Checklist Items sheet", true, 1024);
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, ChecklistItems = [item], SuppliedFields = Columns(nameof(TransferTask.ChecklistItems)) }],
        };

        var plan = Plan(document);

        plan.Findings.Should().BeEmpty();
        var planned = plan.Tasks.Should().ContainSingle().Subject;
        planned.Record.ChangeRequestId.Should().Be(cr);
        planned.Record.ChecklistItems.Select(i => (i.Id.Value, i.Text.Value, i.IsCompleted)).Should().Equal((item.Id, item.Text, true));
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_tabular_row_without_the_change_request_column_merges_a_linked_task_and_keeps_its_link()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, Notes = "Merged", SuppliedFields = Columns("Id", "Notes") }],
        };

        var plan = Plan(document);

        plan.Findings.Should().BeEmpty();
        var planned = plan.Tasks.Should().ContainSingle().Subject;
        (planned.Record.Notes?.Value, planned.Record.ChangeRequestId).Should().Be(("Merged", cr));
    }

    [Fact]
    public async Task A_tabular_row_with_a_blank_change_request_id_for_an_unlinked_task_is_not_a_conflict()
    {
        var task = await _workspace.AddTaskAsync();
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, Notes = "Merged", SuppliedFields = Columns("Id", TransferColumns.ChangeRequestId, "Notes") }],
        };

        var plan = Plan(document);

        plan.Findings.Should().BeEmpty();
        var planned = plan.Tasks.Should().ContainSingle().Subject;
        (planned.Record.Notes?.Value, planned.Record.ChangeRequestId).Should().Be(("Merged", (ChangeRequestId?)null));
        plan.Links.Should().BeEmpty();
    }

    [Fact]
    [Trait("Req", "AC-29")]
    public async Task A_tabular_row_that_names_the_same_cr_for_a_linked_task_merges_without_a_conflict()
    {
        var cr = await _workspace.AddCrAsync();
        var task = await _workspace.AddLinkedTaskAsync(cr);
        var document = new TransferDocument
        {
            Tasks = [new TransferTask { Id = task.Value, Notes = "Merged", SuppliedFields = Columns("Id", TransferColumns.ChangeRequestId, "Notes") }],
            Links = [new TransferLink(task.Value, cr.Value, null)],
        };

        var plan = Plan(document);

        plan.Findings.Should().BeEmpty();
        plan.Tasks.Should().ContainSingle().Which.Record.ChangeRequestId.Should().Be(cr);
        plan.Links.Should().BeEmpty("the existing link is kept, not written again");
    }
}
