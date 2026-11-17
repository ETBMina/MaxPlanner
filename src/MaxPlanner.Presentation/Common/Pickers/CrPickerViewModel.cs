using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Services;
using MaxPlanner.Presentation.Common.Store;

namespace MaxPlanner.Presentation.Common.Pickers;

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
