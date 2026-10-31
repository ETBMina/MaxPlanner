using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>A chip in a token picker (design §5.0); <see cref="IsNew"/> names one that is created only on Save (B-02.6).</summary>
public sealed class TokenViewModel(Guid id, string name, bool isNew, string removeName, bool isPerson = false, bool isTag = false)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public bool IsNew { get; } = isNew;

    /// <summary>"Remove Alice": the chip button's accessible name.</summary>
    public string RemoveName { get; } = removeName;

    /// <summary>A person chip shows the person's avatar before the name.</summary>
    public bool IsPerson { get; } = isPerson;

    /// <summary>A tag chip is a pill in the tag's palette color (design §6.1).</summary>
    public bool IsTag { get; } = isTag;
}

/// <summary>A suggestion row: an existing name, or "Add 'name'" for a new one. A person row shows the avatar.</summary>
public sealed record TokenSuggestion(Guid? ExistingId, string Name, string Display, bool IsPerson = false)
{
    /// <summary>An existing person: the row shows their avatar ("Add 'name'" rows show the Add glyph instead).</summary>
    public bool ShowsAvatar => IsPerson && ExistingId is not null;

    public bool IsAddNew => ExistingId is null;

    public override string ToString() => Display;
}

/// <summary>
/// New names typed into the pickers of one draft, so the same new name gets one ID even when it is used
/// as both Designer and Developer. The IDs are sent with the save; nothing is created before it (B-02.6).
/// </summary>
public sealed class NewNameRegistry
{
    private readonly Dictionary<string, (Guid Id, string Name)> _byName = new(StringComparer.Ordinal);

    public Guid IdFor(string name)
    {
        var key = Normalize(name);
        if (!_byName.TryGetValue(key, out var entry))
        {
            entry = (Guid.NewGuid(), name.Trim());
            _byName[key] = entry;
        }

        return entry.Id;
    }

    /// <summary>The new names among <paramref name="ids"/>, in first-use order.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Used(IEnumerable<Guid> ids)
    {
        var used = ids.ToHashSet();
        return [.. _byName.Values.Where(e => used.Contains(e.Id))];
    }

    internal static string Normalize(string name) => name.Trim().ToUpperInvariant();
}

/// <summary>
/// The TokenPicker (ADR 0021, design §5.0): case-insensitive suggestions from existing people or tags, an
/// "Add 'name'" suggestion for a new one, and removable chips. Use on the UI thread.
/// With <see cref="IsSingleSelection"/> it is the person dropdown of design §5.0: <see cref="Query"/> shows the chosen
/// person's name while the user is not typing, and Esc or leaving the box with other text restores it.
/// </summary>
public sealed partial class TokenPickerViewModel : ObservableObject
{
    private const int MaxSuggestions = 8;

    private readonly ILocalizedStrings _strings;
    private readonly Func<IEnumerable<(Guid Id, string Name)>> _existing;
    private readonly NewNameRegistry _registry;
    private readonly ObservableCollection<TokenViewModel> _tokens = [];

    public TokenPickerViewModel(ILocalizedStrings strings, Func<IEnumerable<(Guid Id, string Name)>> existing, NewNameRegistry registry)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
        _existing = existing ?? throw new ArgumentNullException(nameof(existing));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Tokens = new ReadOnlyObservableCollection<TokenViewModel>(_tokens);
        _tokens.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsShowingSelection));
            OnPropertyChanged(nameof(ShownPersonName));
        };
    }

    /// <summary>Raised when a chip was added or removed by the user.</summary>
    public event EventHandler? Changed;

    public ReadOnlyObservableCollection<TokenViewModel> Tokens { get; }

    public ObservableCollection<TokenSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    /// <summary>
    /// A single-person picker (design §5.0: CR Owner, review Developer, task Designer and Developer): a dropdown whose
    /// box shows the chosen person. Choosing a name replaces it, with the same suggestions and add-new behavior as the
    /// multi pickers.
    /// </summary>
    public bool IsSingleSelection { get; init; }

    /// <summary>
    /// False offers existing names only. Custom People/Tags field values refer to existing lookups only (the
    /// add-new route of B-02.6 belongs to the built-in Designer, Developer, and Tags fields).
    /// </summary>
    public bool AllowNew { get; init; } = true;

    /// <summary>A people picker: chips and suggestions show avatars. False for tags and services.</summary>
    public bool IsPeople { get; init; }

    /// <summary>A tags picker: chips are tag pills (design §6.1). False for people and services.</summary>
    public bool IsTags { get; init; }

    public IReadOnlyList<Guid> Ids => [.. _tokens.Select(t => t.Id)];

    /// <summary>The chosen person's name in a single-person picker; empty when nobody is chosen.</summary>
    public string SelectedName => _tokens.Count > 0 ? _tokens[0].Name : string.Empty;

    public bool HasSelection => _tokens.Count > 0;

    /// <summary>True when a single-person box shows the chosen name (empty when nobody is chosen) rather than typed text.</summary>
    public bool IsShowingSelection => string.Equals(Query, SelectedName, StringComparison.Ordinal);

    /// <summary>
    /// The name behind the avatar in the box: the chosen person while the box shows them, and empty (the generic person
    /// glyph) while the user types or the placeholder shows.
    /// </summary>
    public string ShownPersonName => IsShowingSelection ? SelectedName : string.Empty;

    /// <summary>
    /// True while the ViewModel itself sets <see cref="Query"/> (showing the chosen name after a pick, Esc, or a rebase),
    /// so the view can tell that text change from typing and leave the list closed.
    /// </summary>
    public bool IsSyncingQuery { get; private set; }

    /// <summary>The box's placeholder: "Choose a person" in a single-person picker, none in a chip picker.</summary>
    public string PlaceholderText => IsSingleSelection ? _strings.GetString("PersonPicker_Placeholder") : string.Empty;

    /// <summary>The box's help text in a single-person picker: F4 or Alt+Down opens the list.</summary>
    public string HelpText => IsSingleSelection ? _strings.GetString("PersonPicker_HelpText") : string.Empty;

    /// <summary>
    /// The box's accessible name in a single-person picker: "Owner: Alice" with a choice, otherwise "Owner". The visual
    /// required marker ("Owner *") is left out; required fields are announced through their validation.
    /// </summary>
    public string AccessibleName(string header)
    {
        var label = (header ?? string.Empty).TrimEnd(' ', '*');
        return HasSelection && label.Length > 0 ? _strings.Format("PersonPicker_Name", label, SelectedName) : label;
    }

    /// <summary>Replaces the chips without raising <see cref="Changed"/> (loading or rebasing a draft).</summary>
    public void Load(IEnumerable<(Guid Id, string Name, bool IsNew)> tokens)
    {
        // A single-person box shows the loaded name, unless the user is typing in it (ADR 0010 keeps drafts).
        var wasShowingSelection = IsShowingSelection;
        _tokens.Clear();
        foreach (var (id, name, isNew) in tokens)
        {
            _tokens.Add(Token(id, name, isNew));
        }

        // Only the text is synced: an open list keeps its rows, so a rebase does not close it under the user.
        if (IsSingleSelection && wasShowingSelection)
        {
            SetQuerySilently(SelectedName);
        }
    }

    public bool Choose(TokenSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (IsReadOnly)
        {
            return false;
        }

        var id = suggestion.ExistingId ?? _registry.IdFor(suggestion.Name);
        if (_tokens.Any(t => t.Id == id))
        {
            ResetQuery();
            return false;
        }

        if (IsSingleSelection)
        {
            _tokens.Clear();
        }

        _tokens.Add(Token(id, suggestion.Name.Trim(), suggestion.ExistingId is null));
        ResetQuery();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Enter in the box: the exact existing name if there is one, otherwise a new name (B-02.6). In a single-person
    /// picker, Enter in an emptied box clears the choice.
    /// </summary>
    public bool SubmitQuery()
    {
        if (IsSingleSelection && Query.Trim().Length == 0)
        {
            return Clear();
        }

        return Suggest(Query).FirstOrDefault() is { } best && Choose(best);
    }

    public void Remove(TokenViewModel token)
    {
        if (!IsReadOnly && _tokens.Remove(token))
        {
            if (IsSingleSelection)
            {
                Revert();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Clears a single-person choice. Only optional fields stay empty; a required one reports it on Save.</summary>
    public bool Clear()
    {
        if (IsReadOnly || _tokens.Count == 0)
        {
            Revert();
            return false;
        }

        Remove(_tokens[0]);
        return true;
    }

    /// <summary>Esc: a single-person box shows the chosen person again and a chip picker's box empties; the list closes.</summary>
    public void Revert()
    {
        SetQuerySilently(IsSingleSelection ? SelectedName : string.Empty);
        Suggestions.Clear();
    }

    /// <summary>
    /// Focus left a single-person box, or its editor saves (Ctrl+Enter saves without moving focus). An emptied box clears
    /// the choice and an exact existing name (any case) is chosen; any other text is dropped and the chosen person shows
    /// again. A new name is added only by a pick or Enter. Once committed the box shows the choice, so a second call
    /// (the late LostFocus after a save) changes nothing.
    /// </summary>
    public bool CommitText()
    {
        if (!IsSingleSelection)
        {
            return false;
        }

        if (IsShowingSelection)
        {
            Suggestions.Clear();
            return false;
        }

        var trimmed = Query.Trim();
        if (trimmed.Length == 0)
        {
            return Clear();
        }

        var key = NewNameRegistry.Normalize(trimmed);
        if (_existing().FirstOrDefault(e => NewNameRegistry.Normalize(e.Name) == key) is { Name: not null } exact)
        {
            return Choose(new TokenSuggestion(exact.Id, exact.Name, exact.Name, IsPeople));
        }

        Revert();
        return false;
    }

    /// <summary>
    /// Fills <see cref="Suggestions"/> for the current query when the user focuses or clicks the box. An empty query
    /// lists every existing name that is not already a chip, so the user can pick without typing; a non-empty query
    /// filters exactly as typing does. A single-person box that shows the chosen name lists everyone, chosen included.
    /// </summary>
    /// <param name="all">The dropdown's chevron, F4, or Alt+Down: every name, whatever the box holds.</param>
    public void ShowSuggestions(bool all = false)
    {
        if (!IsReadOnly)
        {
            var listsAll = all || Query.Trim().Length == 0 || (IsSingleSelection && IsShowingSelection);
            Fill(listsAll ? Available() : Suggest(Query));
        }
    }

    // An emptied query (after Choose, or a programmatic reset) clears the list so it does not reopen by itself; only
    // ShowSuggestions lists the available names.
    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(IsShowingSelection));
        OnPropertyChanged(nameof(ShownPersonName));
        if (!IsSyncingQuery)
        {
            Fill(Suggest(value));
        }
    }

    // The query may already be empty when a name was picked from the unfiltered list, so the list is cleared too.
    private void ResetQuery() => Revert();

    // Showing the chosen name is not typing, so it does not fill the suggestions.
    private void SetQuerySilently(string value)
    {
        IsSyncingQuery = true;
        try
        {
            Query = value;
        }
        finally
        {
            IsSyncingQuery = false;
        }
    }

    private void Fill(IEnumerable<TokenSuggestion> suggestions)
    {
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }
    }

    // A single-person list keeps the chosen person, as a ComboBox does; a chip picker leaves out its chips.
    private IEnumerable<TokenSuggestion> Available() => _existing()
        .Where(e => IsSingleSelection || _tokens.All(t => t.Id != e.Id))
        .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(e => new TokenSuggestion(e.Id, e.Name, e.Name, IsPeople));

    private IEnumerable<TokenSuggestion> Suggest(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        var key = NewNameRegistry.Normalize(trimmed);
        var existing = _existing().ToList();
        if (existing.FirstOrDefault(e => NewNameRegistry.Normalize(e.Name) == key) is { Name: not null } exact)
        {
            yield return new TokenSuggestion(exact.Id, exact.Name, exact.Name, IsPeople);
        }
        else if (AllowNew && trimmed.Length <= DomainLimits.LookupName)
        {
            yield return new TokenSuggestion(null, trimmed, _strings.Format("TokenPicker_AddNew", trimmed), IsPeople);
        }

        foreach (var match in existing
            .Where(e => NewNameRegistry.Normalize(e.Name) != key && e.Name.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSuggestions))
        {
            yield return new TokenSuggestion(match.Id, match.Name, match.Name, IsPeople);
        }
    }

    private TokenViewModel Token(Guid id, string name, bool isNew) => new(id, name, isNew, _strings.Format("TokenPicker_Remove", name), IsPeople, IsTags);
}

/// <summary>A CR row in the link picker (design §5.6): number, title, owner, and linked-task count. Never "occupied".</summary>
public sealed record CrChoice(ChangeRequestId Id, string Number, string Title, string OwnerName, string SquadName, string? TaskNumber, int LinkedTaskCount, long Revision, string Secondary)
{
    public override string ToString() => Number + " · " + Title;
}

/// <summary>Step 1 of Link to CR (design §5.6): search by number, title, or owner and pick one CR.</summary>
public sealed partial class CrPickerViewModel : ObservableObject
{
    private const int MaxResults = 50;

    private readonly WorkspaceProjectionStore _store;
    private readonly ILocalizedStrings _strings;

    public CrPickerViewModel(WorkspaceProjectionStore store, ILocalizedStrings strings)
    {
        _store = store;
        _strings = strings;
    }

    public ObservableCollection<CrChoice> Results { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial CrChoice? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public bool HasNoResults => Results.Count == 0;

    /// <summary>Design §5.6 confirmation: "The title becomes 'Card limits API'".</summary>
    public string TitleLine(CrChoice? choice) => choice is null ? string.Empty : _strings.Format("LinkCr_TitleLine", choice.Title);

    /// <summary>Design §5.6 confirmation: "Task number, squad and owner come from CR-1042".</summary>
    public string InheritLine(CrChoice? choice) => choice is null ? string.Empty : _strings.Format("LinkCr_InheritLine", choice.Number);

    /// <summary>The current committed state of a CR, for the confirmation preview and a refresh after a change (T-13).</summary>
    public CrChoice? Current(ChangeRequestId id) => _store.ChangeRequests.Lookup(id) is { HasValue: true } cr ? Choice(cr.Value) : null;

    public void Refresh()
    {
        var query = Query.Trim();
        var selected = Selected?.Id;
        Results.Clear();
        foreach (var cr in _store.ChangeRequests.Items
            .Where(c => query.Length == 0
                || c.Number.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || c.Owner.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(c => c.NormalizedNumber, StringComparer.Ordinal)
            .Take(MaxResults))
        {
            Results.Add(Choice(cr));
        }

        Selected = Results.FirstOrDefault(r => r.Id == selected);
        OnPropertyChanged(nameof(HasNoResults));
    }

    partial void OnQueryChanged(string value) => Refresh();

    private CrChoice Choice(ChangeRequestReadModel cr) => new(
        cr.Id,
        cr.Number,
        cr.Title,
        cr.Owner.Name,
        cr.SquadName,
        cr.TaskNumber,
        cr.LinkedTaskCount,
        cr.Revision,
        _strings.Format(cr.LinkedTaskCount == 1 ? "LinkCr_SecondaryOne" : "LinkCr_Secondary", cr.Owner.Name, cr.LinkedTaskCount));
}
