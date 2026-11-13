using AwesomeAssertions;
using MaxPlanner.Application.Settings;
using MaxPlanner.Presentation.Checklists;
using MaxPlanner.Presentation.Settings;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Workspace;

/// <summary>
/// The two inline renames (design §4.5 checklist items, §4.6 Settings lookup rows): begin copies the saved text into
/// the draft, commit saves a changed draft and closes, closes an unchanged draft without writing, keeps an empty draft
/// open with its error, and cancel closes without saving. The rules differ in detail between the two (the checklist
/// compares normalized line breaks, Settings compares the trimmed draft), and these tests pin each as it is.
/// </summary>
public sealed class InlineRenameTests
{
    // ----- Checklist items -----
    [Fact]
    [Trait("Req", "B-09")]
    public void Beginning_a_checklist_rename_copies_the_text_into_the_draft_and_clears_the_error()
    {
        using var h = Checklist(out var item);

        item.BeginRename();

        (item.IsRenaming, item.RenameText, item.RenameError, item.HasRenameError).Should().Be((true, "Draft spec", (string?)null, false));
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_changed_checklist_rename_is_saved_and_closes()
    {
        using var h = Checklist(out var item);
        item.BeginRename();
        item.RenameText = "Final spec";

        h.Await(item.CommitRenameAsync()).Should().BeTrue();
        h.Settle();

        (item.IsRenaming, item.Text, item.RenameError).Should().Be((false, "Final spec", (string?)null));
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void An_unchanged_checklist_rename_closes_without_writing()
    {
        using var h = Checklist(out var item);
        var sequence = h.Store.Sequence;
        item.BeginRename();

        h.Await(item.CommitRenameAsync()).Should().BeTrue();
        h.Settle();

        item.IsRenaming.Should().BeFalse();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_checklist_rename_that_differs_only_by_surrounding_spaces_is_not_unchanged()
    {
        using var h = Checklist(out var item);
        item.BeginRename();

        item.RenameText = " Draft spec ";

        item.IsRenameUnchanged.Should().BeFalse("the checklist compares line breaks, not trimmed text");
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void An_empty_checklist_rename_stays_open_with_its_error_and_keeps_the_text()
    {
        using var h = Checklist(out var item);
        var sequence = h.Store.Sequence;
        item.BeginRename();
        item.RenameText = string.Empty;

        h.Await(item.CommitRenameAsync()).Should().BeFalse();
        h.Settle();

        (item.IsRenaming, item.Text, item.RenameText).Should().Be((true, "Draft spec", string.Empty));
        item.RenameError.Should().NotBeNullOrEmpty();
        item.HasRenameError.Should().BeTrue();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void Cancelling_a_checklist_rename_closes_it_and_clears_the_error_without_saving()
    {
        using var h = Checklist(out var item);
        item.BeginRename();
        item.RenameText = string.Empty;
        h.Await(item.CommitRenameAsync()).Should().BeFalse();
        item.RenameText = "Something else";

        item.CancelRename();
        h.Settle();

        (item.IsRenaming, item.Text, item.RenameError).Should().Be((false, "Draft spec", (string?)null));
    }

    [Fact]
    [Trait("Req", "B-09")]
    public void A_closed_checklist_rename_does_not_commit()
    {
        using var h = Checklist(out var item);

        h.Await(item.CommitRenameAsync()).Should().BeFalse();
    }

    // ----- Settings lookup rows -----
    [Fact]
    [Trait("Req", "B-04")]
    public void Beginning_a_settings_rename_copies_the_name_into_the_draft_and_clears_the_error()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        row.RenameError = "Earlier error";

        row.BeginRename();

        (row.IsRenaming, row.IsNotRenaming, row.RenameText).Should().Be((true, false, row.Name));
        row.RenameError.Should().BeNull();
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_changed_settings_rename_is_saved_and_closes()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        row.BeginRename();
        row.RenameText = "Renamed status";

        h.Await(settings.CommitRenameAsync(row)).Should().BeTrue();
        h.Settle();

        (row.IsRenaming, row.RenameError, row.IsBusy).Should().Be((false, (string?)null, false));
        settings.TaskStatuses.Rows[0].Name.Should().Be("Renamed status");
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void An_unchanged_settings_rename_with_surrounding_spaces_closes_without_writing()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        var sequence = h.Store.Sequence;
        row.BeginRename();
        row.RenameText = "  " + row.Name + " ";

        h.Await(settings.CommitRenameAsync(row)).Should().BeTrue();
        h.Settle();

        row.IsRenaming.Should().BeFalse();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void An_empty_settings_rename_stays_open_with_its_error()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        var name = row.Name;
        var sequence = h.Store.Sequence;
        row.BeginRename();
        row.RenameText = "   ";

        h.Await(settings.CommitRenameAsync(row)).Should().BeFalse();
        h.Settle();

        (row.IsRenaming, row.IsBusy, row.Name).Should().Be((true, false, name));
        row.RenameError.Should().NotBeNullOrEmpty();
        h.Store.Sequence.Should().Be(sequence, "nothing was written");
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void Cancelling_a_settings_rename_closes_it_and_clears_the_error_without_saving()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        var name = row.Name;
        row.BeginRename();
        row.RenameText = string.Empty;
        h.Await(settings.CommitRenameAsync(row)).Should().BeFalse();

        row.CancelRename();
        h.Settle();

        (row.IsRenaming, row.RenameError, row.Name).Should().Be((false, (string?)null, name));
    }

    [Fact]
    [Trait("Req", "B-04")]
    public void A_settings_rename_ignores_cancel_and_commit_while_its_command_runs()
    {
        using var h = WorkspaceHarness.Create();
        using var settings = Settings(h);
        var row = settings.TaskStatuses.Rows[0];
        row.BeginRename();
        row.RenameText = "Renamed status";
        row.IsBusy = true;

        row.CancelRename();
        h.Await(settings.CommitRenameAsync(row)).Should().BeFalse();

        row.IsRenaming.Should().BeTrue();
    }

    private static WorkspaceHarness Checklist(out ChecklistItemViewModel item)
    {
        var h = WorkspaceHarness.Create(async s => await s.TaskAsync("Task", "Service", null, "Draft spec"));
        var checklists = h.Workspace.Checklists;
        h.PumpUntil(() => checklists.Groups.Count == 1);
        item = checklists.Groups.Single().OpenItems.Single();
        return h;
    }

    private static SettingsPageViewModel Settings(WorkspaceHarness h)
    {
        var settings = new SettingsPageViewModel(h.Workspace.Context, new SettingsServices(SettingsUseCases.Create(h.Coordinator, h.Dataset.Snapshots), null, null, null));
        h.PumpUntil(() => settings.TaskStatuses.Rows.Count > 0);
        h.Settle();
        return settings;
    }
}
