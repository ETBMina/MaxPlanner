using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Common.Dialogs;
using MaxPlanner.Presentation.Tests.Testing;
using MaxPlanner.Presentation.Workspace;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The shared conflict steps of <see cref="ConflictEditorViewModelBase{TFollowUp}"/> (design §5.10) against a minimal
/// editor: when each step opens, what Apply and Reload hand to the editor, and what they reset afterwards.
/// </summary>
[Trait("Req", "B-02.9")]
public sealed class ConflictEditorViewModelBaseTests
{
    [Fact]
    public void Review_opens_only_during_a_conflict_with_a_latest_record()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new FakeEditor(h.Workspace.Context);

        editor.ReviewConflict();
        editor.Steps.Should().BeEmpty("there is no conflict");

        editor.StartConflict();
        editor.Rows = null;
        editor.ReviewConflict();
        editor.Steps.Should().BeEmpty("there is no latest record to compare with");

        editor.Rows = [Row("Title"), Row("Notes")];
        editor.ReviewConflictCommand.Execute(null);
        editor.Steps.Should().Equal(ConflictStep.Review);
        editor.ConflictRows.Select(r => r.Key).Should().Equal("Title", "Notes");
        editor.Raised.Should().Be(1);
    }

    [Fact]
    public void A_second_review_replaces_the_rows()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new FakeEditor(h.Workspace.Context);
        editor.StartConflict();
        editor.Rows = [Row("Title")];
        editor.ReviewConflict();

        editor.Rows = [Row("Notes")];
        editor.ReviewConflict();

        editor.ConflictRows.Select(r => r.Key).Should().Equal("Notes");
    }

    [Fact]
    public void Apply_hands_the_use_mine_keys_to_the_editor_then_ends_the_conflict_and_returns_to_the_form()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new FakeEditor(h.Workspace.Context);
        editor.StartConflict();
        editor.Rows = [Row("Title"), Row("Notes"), Row("Status")];
        editor.ReviewConflict();
        editor.ConflictRows.Single(r => r.Key == "Notes").UseMine = false;

        editor.ApplyConflictReviewCommand.Execute(null);

        editor.Kept.Should().BeEquivalentTo(["Title", "Status"]);
        (editor.IsConflict, editor.SaveError).Should().Be((false, null));
        editor.ConflictRows.Should().BeEmpty();
        editor.Steps.Should().Equal(ConflictStep.Review, ConflictStep.Form);
        editor.DirtyChecks.Should().BeGreaterThan(0, "the applied draft is compared with the record again");
    }

    [Fact]
    public void Apply_without_a_latest_record_changes_nothing()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new FakeEditor(h.Workspace.Context);
        editor.StartConflict();
        editor.Rows = [Row("Title")];
        editor.ReviewConflict();
        editor.CanApply = false;

        editor.ApplyConflictReview();

        (editor.IsConflict, editor.SaveError).Should().Be((true, "conflict"));
        editor.ConflictRows.Should().ContainSingle();
        editor.Steps.Should().Equal(ConflictStep.Review);
    }

    [Fact]
    public void Reload_asks_first_then_reloads_ends_the_conflict_and_returns_to_the_form()
    {
        using var h = WorkspaceHarness.Create(_ => Task.CompletedTask);
        using var editor = new FakeEditor(h.Workspace.Context);
        editor.StartConflict();

        editor.BeginReloadCommand.Execute(null);
        editor.Steps.Should().Equal(ConflictStep.ReloadConfirm);
        editor.Reloads.Should().Be(0, "Reload discards the draft only after the confirmation");

        editor.ConfirmReloadCommand.Execute(null);
        editor.Reloads.Should().Be(1);
        (editor.IsConflict, editor.SaveError).Should().Be((false, null));
        editor.Steps.Should().Equal(ConflictStep.ReloadConfirm, ConflictStep.Form);
        editor.Raised.Should().Be(2);
    }

    private static ConflictRowViewModel Row(string key) => new(key, key, "mine", "saved");

    private enum FollowUp
    {
        None,
    }

    private sealed class FakeEditor(WorkspaceContext context) : ConflictEditorViewModelBase<FollowUp>(context, isNew: false, "Record_Task")
    {
        public IReadOnlyList<ConflictRowViewModel>? Rows { get; set; }

        public bool CanApply { get; set; } = true;

        public List<ConflictStep> Steps { get; } = [];

        public IReadOnlySet<string>? Kept { get; private set; }

        public int Reloads { get; private set; }

        public int Raised { get; private set; }

        public int DirtyChecks { get; private set; }

        public override bool CanSave => true;

        protected override bool NeedsClosePrompt => false;

        public void StartConflict()
        {
            IsConflict = true;
            SaveError = "conflict";
        }

        protected override IReadOnlyList<ConflictRowViewModel>? BuildConflictRows() => Rows;

        protected override bool ApplyReviewToDraft(IReadOnlySet<string> keep)
        {
            if (!CanApply)
            {
                return false;
            }

            Kept = keep;
            return true;
        }

        protected override void ReloadLatest() => Reloads++;

        protected override void ShowConflictStep(ConflictStep conflictStep) => Steps.Add(conflictStep);

        protected override void RaiseState() => Raised++;

        protected override bool ComputeIsDirty()
        {
            DirtyChecks++;
            return false;
        }

        protected override Task<(AppError? Error, long Sequence)> ExecuteSaveAsync(OperationId operation) => Task.FromResult<(AppError?, long)>((null, 0));

        protected override void OnSaved()
        {
        }

        protected override void ClearFieldErrors()
        {
        }

        protected override void ShowFieldErrors(IReadOnlyList<DomainError> errors)
        {
        }

        protected override void OnCommitted()
        {
        }
    }
}
