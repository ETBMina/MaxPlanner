using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Tasks;

/// <summary>A chip in a token picker (design §5.0); <see cref="IsNew"/> names one that is created only on Save (B-02.6).</summary>
public sealed class TokenViewModel(Guid id, string name, bool isNew, string removeName)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public bool IsNew { get; } = isNew;

    /// <summary>"Remove Alice": the chip button's accessible name.</summary>
    public string RemoveName { get; } = removeName;
}

/// <summary>A suggestion row: an existing name, or "Add 'name'" for a new one.</summary>
public sealed record TokenSuggestion(Guid? ExistingId, string Name, string Display)
{
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
    }

    /// <summary>Raised when a chip was added or removed by the user.</summary>
    public event EventHandler? Changed;

    public ReadOnlyObservableCollection<TokenViewModel> Tokens { get; }

    public ObservableCollection<TokenSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    public IReadOnlyList<Guid> Ids => [.. _tokens.Select(t => t.Id)];

    /// <summary>Replaces the chips without raising <see cref="Changed"/> (loading or rebasing a draft).</summary>
    public void Load(IEnumerable<(Guid Id, string Name, bool IsNew)> tokens)
    {
        _tokens.Clear();
        foreach (var (id, name, isNew) in tokens)
        {
            _tokens.Add(Token(id, name, isNew));
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
            Query = string.Empty;
            return false;
        }

        _tokens.Add(Token(id, suggestion.Name.Trim(), suggestion.ExistingId is null));
        Query = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Enter in the box: the exact existing name if there is one, otherwise a new name (B-02.6).</summary>
    public bool SubmitQuery() => Suggest(Query).FirstOrDefault() is { } best && Choose(best);

    public void Remove(TokenViewModel token)
    {
        if (!IsReadOnly && _tokens.Remove(token))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnQueryChanged(string value)
    {
        Suggestions.Clear();
        foreach (var suggestion in Suggest(value))
        {
            Suggestions.Add(suggestion);
        }
    }

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
            yield return new TokenSuggestion(exact.Id, exact.Name, exact.Name);
        }
        else if (trimmed.Length <= DomainLimits.LookupName)
        {
            yield return new TokenSuggestion(null, trimmed, _strings.Format("TokenPicker_AddNew", trimmed));
        }

        foreach (var match in existing
            .Where(e => NewNameRegistry.Normalize(e.Name) != key && e.Name.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSuggestions))
        {
            yield return new TokenSuggestion(match.Id, match.Name, match.Name);
        }
    }

    private TokenViewModel Token(Guid id, string name, bool isNew) => new(id, name, isNew, _strings.Format("TokenPicker_Remove", name));
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
