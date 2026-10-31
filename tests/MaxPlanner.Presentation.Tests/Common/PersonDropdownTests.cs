using AwesomeAssertions;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Tasks;
using MaxPlanner.Presentation.Tests.Testing;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>Design §5.0 "Single person": the single-selection TokenPickerViewModel behind the person dropdown.</summary>
[Trait("Req", "B-02.6")]
public sealed class PersonDropdownTests
{
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();
    private static readonly Guid AlistairId = Guid.NewGuid();

    private readonly List<(Guid Id, string Name)> _people = [(BobId, "Bob"), (AliceId, "Alice"), (AlistairId, "Alistair")];
    private readonly NewNameRegistry _registry = new();
    private int _changes;

    [Fact]
    public void The_box_shows_the_loaded_person_and_their_name_in_the_accessible_name()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);

        picker.Query.Should().Be("Alice");
        picker.SelectedName.Should().Be("Alice");
        picker.IsShowingSelection.Should().BeTrue();
        picker.Suggestions.Should().BeEmpty("loading does not open the list");
        picker.AccessibleName("Owner *").Should().Be("Owner: Alice");
        _changes.Should().Be(0);
    }

    [Fact]
    public void Nobody_chosen_shows_the_placeholder_and_the_plain_header()
    {
        var picker = Picker();

        picker.Query.Should().BeEmpty();
        picker.HasSelection.Should().BeFalse();
        picker.PlaceholderText.Should().Be("PersonPicker_Placeholder");
        picker.HelpText.Should().Be("PersonPicker_HelpText");
        picker.AccessibleName("Designer").Should().Be("Designer");
        picker.AccessibleName("Owner *").Should().Be("Owner", "the visual required marker is not read");
        picker.ShownPersonName.Should().BeEmpty();
    }

    [Fact]
    public void The_box_avatar_shows_the_chosen_person_only_while_the_box_shows_them()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        var changed = new List<string?>();
        picker.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        picker.ShownPersonName.Should().Be("Alice");
        picker.Query = "Bo";
        picker.ShownPersonName.Should().BeEmpty("typed text shows the generic person glyph");
        changed.Should().Contain([nameof(TokenPickerViewModel.IsShowingSelection), nameof(TokenPickerViewModel.ShownPersonName)]);

        picker.Revert();
        picker.ShownPersonName.Should().Be("Alice");
    }

    [Fact]
    public void A_rebase_leaves_an_open_list_alone()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.ShowSuggestions();

        picker.Load([(AliceId, "Alice Smith", false)]);

        picker.Query.Should().Be("Alice Smith");
        picker.Suggestions.Should().HaveCount(3, "the rows under the user stay put");
    }

    [Fact]
    public void A_rebase_that_removes_the_chosen_person_empties_the_box_as_the_view_model_not_as_typing()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        var syncedEmpty = false;
        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TokenPickerViewModel.Query) && picker.Query.Length == 0)
            {
                syncedEmpty = picker.IsSyncingQuery;
            }
        };

        picker.Load([]);

        picker.Query.Should().BeEmpty();
        syncedEmpty.Should().BeTrue("the view leaves the list closed for text the ViewModel set");
        picker.Suggestions.Should().BeEmpty("nothing fills the list by itself");
        picker.IsSyncingQuery.Should().BeFalse();
    }

    [Fact]
    public void Opening_the_box_that_shows_the_choice_lists_everyone_including_the_chosen_person()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);

        picker.ShowSuggestions();

        picker.Suggestions.Select(s => s.Name).Should().Equal(["Alice", "Alistair", "Bob"]);
        picker.Suggestions.Should().OnlyContain(s => s.ShowsAvatar);
    }

    [Fact]
    public void Typing_filters_case_insensitively_and_offers_add_new_when_no_name_matches_exactly()
    {
        var picker = Picker();
        picker.Load([(BobId, "Bob", false)]);

        picker.Query = "ali";

        picker.Suggestions.Select(s => s.Display).Should().Equal(["TokenPicker_AddNew", "Alice", "Alistair"]);
        picker.Suggestions[0].IsAddNew.Should().BeTrue();

        picker.Query = "ALICE";
        picker.Suggestions.Select(s => s.Name).Should().Equal(["Alice"], "an exact match, in any case, offers no Add");
    }

    [Fact]
    public void The_chevron_lists_everyone_even_while_text_is_typed()
    {
        var picker = Picker();
        picker.Query = "bo";

        picker.ShowSuggestions(all: true);

        picker.Suggestions.Should().HaveCount(3);
    }

    [Fact]
    public void Picking_replaces_the_choice_and_the_box_shows_the_new_name()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.Query = "bob";

        picker.SubmitQuery().Should().BeTrue();

        picker.Ids.Should().Equal([BobId]);
        picker.Query.Should().Be("Bob");
        picker.Suggestions.Should().BeEmpty();
        picker.AccessibleName("Developer").Should().Be("Developer: Bob");
        _changes.Should().Be(1);
    }

    [Fact]
    public void Picking_the_chosen_person_again_changes_nothing()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.ShowSuggestions();

        picker.Choose(picker.Suggestions.First(s => s.ExistingId == AliceId)).Should().BeFalse();

        picker.Query.Should().Be("Alice");
        picker.Suggestions.Should().BeEmpty();
        _changes.Should().Be(0);
    }

    [Fact]
    public void A_new_name_is_kept_as_new_with_the_shared_registry_id_until_save()
    {
        var picker = Picker();
        var other = Picker();
        picker.Query = "  Dana ";

        picker.SubmitQuery().Should().BeTrue();

        picker.Tokens.Single().IsNew.Should().BeTrue();
        picker.Query.Should().Be("Dana");
        other.Query = "dana";
        other.SubmitQuery().Should().BeTrue();
        other.Ids.Should().Equal(picker.Ids, "the same new name used twice gets one ID");
        _people.Should().HaveCount(3, "nothing is created before Save");
    }

    [Fact]
    public void A_name_over_the_lookup_limit_is_not_offered_as_new()
    {
        var picker = Picker();
        picker.Query = new string('x', DomainLimits.LookupName + 1);

        picker.Suggestions.Should().BeEmpty();
        picker.SubmitQuery().Should().BeFalse();
        picker.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void Esc_restores_the_chosen_person_over_typed_text()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.Query = "Bo";

        picker.Revert();

        picker.Query.Should().Be("Alice");
        picker.Suggestions.Should().BeEmpty();
        picker.Ids.Should().Equal([AliceId]);
        _changes.Should().Be(0);
    }

    [Fact]
    public void Leaving_with_unmatched_text_restores_the_chosen_person()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.Query = "Zed";

        picker.CommitText().Should().BeFalse();

        picker.Query.Should().Be("Alice");
        picker.Ids.Should().Equal([AliceId]);
        _changes.Should().Be(0);
    }

    [Fact]
    public void Leaving_with_an_exact_existing_name_chooses_that_person()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.Query = "bob";

        picker.CommitText().Should().BeTrue();

        picker.Ids.Should().Equal([BobId]);
        picker.Query.Should().Be("Bob");
        _changes.Should().Be(1);
    }

    [Fact]
    public void Leaving_or_pressing_enter_with_an_emptied_box_clears_the_choice()
    {
        var left = Picker();
        left.Load([(AliceId, "Alice", false)]);
        left.Query = string.Empty;
        left.CommitText().Should().BeTrue();
        left.HasSelection.Should().BeFalse();
        left.Query.Should().BeEmpty();

        var entered = Picker();
        entered.Load([(AliceId, "Alice", false)]);
        entered.Query = "  ";
        entered.SubmitQuery().Should().BeTrue();
        entered.HasSelection.Should().BeFalse();
        entered.Query.Should().BeEmpty();
        entered.AccessibleName("Designer").Should().Be("Designer");
        _changes.Should().Be(2);
    }

    [Fact]
    public void A_read_only_box_does_not_clear_or_change()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);
        picker.IsReadOnly = true;
        picker.Query = string.Empty;

        picker.CommitText().Should().BeFalse();

        picker.Ids.Should().Equal([AliceId]);
        picker.Query.Should().Be("Alice");
    }

    [Fact]
    public void A_rebase_updates_the_box_but_keeps_text_the_user_is_typing()
    {
        var picker = Picker();
        picker.Load([(AliceId, "Alice", false)]);

        picker.Load([(AliceId, "Alice Smith", false)]);
        picker.Query.Should().Be("Alice Smith", "a rename shows at once");

        picker.Query = "Bo";
        picker.Load([(AliceId, "Alice", false)]);
        picker.Query.Should().Be("Bo", "a draft in the box survives an incremental update (ADR 0010)");
        picker.SelectedName.Should().Be("Alice");
    }

    [Fact]
    public void A_chip_picker_still_lists_only_names_that_are_not_chips_and_empties_its_box_after_a_pick()
    {
        var tags = new TokenPickerViewModel(new FakeStrings(), () => _people, _registry);
        tags.Load([(AliceId, "Alice", false)]);

        tags.ShowSuggestions();
        tags.Suggestions.Select(s => s.Name).Should().Equal(["Alistair", "Bob"]);

        tags.Query = "bob";
        tags.SubmitQuery().Should().BeTrue();
        tags.Query.Should().BeEmpty();
        tags.Ids.Should().Equal([AliceId, BobId]);
    }

    private TokenPickerViewModel Picker()
    {
        var picker = new TokenPickerViewModel(new FakeStrings().With("PersonPicker_Name", "{0}: {1}"), () => _people, _registry) { IsSingleSelection = true, IsPeople = true };
        picker.Changed += (_, _) => _changes++;
        return picker;
    }
}
