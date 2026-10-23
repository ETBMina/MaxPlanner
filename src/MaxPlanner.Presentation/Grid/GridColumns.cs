using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Fields;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;

namespace MaxPlanner.Presentation.Grid;

/// <summary>Stable column keys; they appear in AutomationIds (<c>Grid_Cell_{Id}_{Key}</c>) and saved layouts, never with labels.</summary>
public static class GridColumns
{
    public const string Kind = "Kind";
    public const string CrNumber = "CrNumber";
    public const string TaskNumber = "TaskNumber";
    public const string Squad = "Squad";
    public const string Owner = "Owner";
    public const string Title = "Title";
    public const string Subtitle = "Subtitle";
    public const string Service = "Service";
    public const string Status = "Status";
    public const string Priority = "Priority";
    public const string Designers = "Designers";
    public const string Developers = "Developers";
    public const string StartDate = "StartDate";
    public const string EndDate = "EndDate";
    public const string Notes = "Notes";
    public const string Tags = "Tags";
    public const string Archived = "Archived";

    /// <summary>Custom field columns are keyed by the field's stable ID, so a label change never breaks a saved layout.</summary>
    public const string CustomPrefix = "Custom_";

    public const double MinWidth = 48;
    public const double MaxWidth = 800;

    /// <summary>B-07 initial columns in default order; active custom fields follow in display order.</summary>
    public static IReadOnlyList<string> Default { get; } =
        [Kind, CrNumber, TaskNumber, Squad, Owner, Title, Subtitle, Service, Status, Priority, Designers, Developers, StartDate, EndDate, Notes, Tags, Archived];

    /// <summary>The built-in columns in default order (kept for callers that enumerate every built-in column).</summary>
    public static IReadOnlyList<string> All => Default;

    /// <summary>
    /// Design §4.3 medium-width priority: identity, status, priority, squad, and owner stay ahead of the rest. The
    /// default order already starts with them, so no reordering is needed when the window narrows.
    /// </summary>
    public static IReadOnlySet<string> MediumPriority { get; } = new HashSet<string>(StringComparer.Ordinal) { Kind, CrNumber, Title, Subtitle, Status, Priority, Squad, Owner };

    public static string Custom(FieldId id) => CustomPrefix + id.Value.ToString("N");

    public static bool IsCustom(string key) => key.StartsWith(CustomPrefix, StringComparison.Ordinal);

    public static double DefaultWidth(string key) => key switch
    {
        Kind => 132,
        CrNumber or TaskNumber => 112,
        Squad or Owner => 140,
        Title => 280,
        Subtitle => 220,
        Service => 180,
        Status => 170,
        Priority => 120,
        Designers or Developers or Tags => 180,
        StartDate or EndDate => 132,
        Notes => 240,
        Archived => 112,
        _ => 160,
    };

    public static double Clamp(double width) => double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : MinWidth;
}

/// <summary>How a column is edited (design §4.3 column table).</summary>
public enum GridEditorKind
{
    None,
    Text,
    Status,
    Priority,
    Date,
    Notes,
    People,
    Tags,
    Custom,
}

/// <summary>Sort state of a column header: Ascending → Descending → default (design §4.3).</summary>
public enum GridSortDirection
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// One Grid column: its key, header, width, visibility, and sort state (B-07). The page keeps every column in
/// configured order, hidden ones included, and persists the layout in <c>configuration.json</c> (T-09).
/// </summary>
public sealed partial class GridColumnViewModel : ObservableObject
{
    public GridColumnViewModel(string key, string header, FieldDefinition? field)
    {
        Key = key;
        Header = header;
        Field = field;
        Width = GridColumns.DefaultWidth(key);
    }

    public string Key { get; }

    /// <summary>The custom field of a custom column; null for built-in columns.</summary>
    public FieldDefinition? Field { get; private set; }

    public bool IsCustom => Field is not null;

    public GridEditorKind EditorKind => Key switch
    {
        GridColumns.Title or GridColumns.Subtitle or GridColumns.Service => GridEditorKind.Text,
        GridColumns.Status => GridEditorKind.Status,
        GridColumns.Priority => GridEditorKind.Priority,
        GridColumns.StartDate or GridColumns.EndDate => GridEditorKind.Date,
        GridColumns.Notes => GridEditorKind.Notes,
        GridColumns.Designers or GridColumns.Developers => GridEditorKind.People,
        GridColumns.Tags => GridEditorKind.Tags,
        _ when Field is not null => GridEditorKind.Custom,
        _ => GridEditorKind.None,
    };

    /// <summary>Editors that open in a flyout (design §4.3: notes, people and tag pickers, multiline and list custom fields).</summary>
    public bool UsesFlyout => EditorKind is GridEditorKind.Notes or GridEditorKind.People or GridEditorKind.Tags
        || Field?.Kind is FieldKind.MultilineText or FieldKind.People or FieldKind.Tags;

    [ObservableProperty]
    public partial string Header { get; set; }

    [ObservableProperty]
    public partial double Width { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial GridSortDirection Sort { get; set; }

    /// <summary>"Sort by Title, currently ascending" (design §4.3): the header's accessible name.</summary>
    [ObservableProperty]
    public partial string AccessibleName { get; set; } = string.Empty;

    internal void UpdateField(FieldDefinition field, string header)
    {
        Field = field;
        Header = header;
        OnPropertyChanged(nameof(Field));
    }
}

/// <summary>
/// The Grid row order: one typed column sort, then the B-07 default (CreatedAt descending, then ID). Sorting only
/// orders this view; Board ranks are never read or written here (B-07: sort never changes Board order).
/// </summary>
internal sealed class GridRowComparer : IComparer<GridRowViewModel>
{
    private readonly string? _column;
    private readonly bool _descending;
    private readonly FieldDefinition? _field;

    /// <param name="field">The custom field of a custom column, so Lookup values sort by option order.</param>
    public GridRowComparer(string? column, bool descending, FieldDefinition? field = null)
    {
        _column = column;
        _descending = descending;
        _field = field;
    }

    public static GridRowComparer Default { get; } = new(null, false);

    public int Compare(GridRowViewModel? x, GridRowViewModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        if (_column is { } column)
        {
            var a = SortValue(x.SortModel, column);
            var b = SortValue(y.SortModel, column);

            // Empty cells always follow filled ones, in both directions.
            if (a is null || b is null)
            {
                if (a is not null || b is not null)
                {
                    return a is null ? 1 : -1;
                }
            }
            else
            {
                var byColumn = CompareValues(a, b);
                if (byColumn != 0)
                {
                    return _descending ? -byColumn : byColumn;
                }
            }
        }

        var byCreated = y.SortModel.CreatedAtUtc.CompareTo(x.SortModel.CreatedAtUtc);
        return byCreated != 0 ? byCreated : x.SortModel.Id.CompareTo(y.SortModel.Id);
    }

    /// <summary>
    /// A typed sort key: text compares case-insensitively in the user's culture, dates and numbers by value,
    /// statuses and priorities by configured order. Empty values sort last in both directions' natural order.
    /// </summary>
    private IComparable? SortValue(WorkItemRecord record, string column)
    {
        var task = record.Task;
        var card = record.Card;
        return column switch
        {
            GridColumns.Kind => (int)record.Kind,
            GridColumns.CrNumber => record.Cr.CrNumber,
            GridColumns.TaskNumber => record.Cr.TaskNumber,
            GridColumns.Squad => record.Cr.SquadName,
            GridColumns.Owner => record.Cr.OwnerName,
            GridColumns.Title => task?.EffectiveTitle ?? card!.ParentTitle,
            GridColumns.Subtitle => card?.Subtitle,
            GridColumns.Service => task?.Service ?? card!.ParentService,
            GridColumns.Status => task?.StatusOrder ?? card!.StatusOrder,
            GridColumns.Priority => task?.PriorityOrder,
            GridColumns.Designers => Names(task?.Designers),
            GridColumns.Developers => Names(task?.Developers),
            GridColumns.Tags => Names(task?.Tags),
            GridColumns.StartDate => task?.StartDate ?? card!.StartDate,
            GridColumns.EndDate => task?.EndDate ?? card!.EndDate,
            GridColumns.Notes => task?.Notes ?? card!.Notes,
            GridColumns.Archived => task?.IsArchived ?? card!.IsParentArchived,
            _ when GridColumns.IsCustom(column) && task is not null => CustomSortValue(task.CustomValues, column),
            _ => null,
        };
    }

    private static string? Names<TId>(IReadOnlyList<LookupRef<TId>>? refs)
        where TId : struct =>
        refs is { Count: > 0 } ? string.Join(", ", refs.Select(r => r.Name)) : null;

    private IComparable? CustomSortValue(IReadOnlyDictionary<FieldId, CustomValue> values, string column) =>
        Guid.TryParseExact(column[GridColumns.CustomPrefix.Length..], "N", out var id) && values.GetValueOrDefault(new FieldId(id)) is { } value
            ? value switch
            {
                NumberCustomValue number => number.Value,
                DateCustomValue date => date.Value,
                TextCustomValue text => text.Value,
                MultilineTextCustomValue text => text.Value,
                UrlCustomValue url => url.Url.ToString(),
                LookupCustomValue lookup => _field?.Options.FirstOrDefault(o => o.Id == lookup.OptionId)?.Order,
                PeopleCustomValue people => people.PersonIds.Count,
                TagsCustomValue tags => tags.TagIds.Count,
                _ => null,
            }
            : null;

    private static int CompareValues(IComparable a, IComparable b) =>
        a is string sa && b is string sb
            ? CultureInfo.CurrentCulture.CompareInfo.Compare(sa, sb, CompareOptions.IgnoreCase)
            : a.GetType() == b.GetType() ? a.CompareTo(b) : 0;
}
