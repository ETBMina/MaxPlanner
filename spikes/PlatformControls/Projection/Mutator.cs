using System.Globalization;
using Microsoft.UI.Dispatching;

namespace PlatformControls.Projection;

public enum MutationMode
{
    Stopped,
    OtherRows,
    MoveFocused,
}

/// <summary>What the mutator needs from the visible view: the focused row and the bound order.</summary>
public interface IMutationContext
{
    ItemViewModel? FocusedItem { get; }

    ItemViewModel? SelectedItem { get; }

    IReadOnlyList<ItemViewModel> Items { get; }

    /// <summary>Called on the UI thread at the start of every tick, before this tick's changes are applied.</summary>
    void Sample(int tick, MutationMode mode);
}

/// <summary>
/// Pushes store updates every 200 ms. OtherRows mode updates neighbours of the focused row each tick,
/// the focused row's committed Title every 10th tick (5.3), and re-sorts unrelated rows on ticks ending in 5 (a row below the
/// anchor moves to the end) and 0 (a far row moves just above the anchor) (5.4). The anchor is the focused row, else the selected row.
/// MoveFocused mode re-sorts the focused row itself on mode ticks 15, 25 and 35 (3, 5 and 7 s after Start, leaving time to focus a row).
/// </summary>
internal sealed class Mutator
{
    private readonly ProjectionStore _store;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _random = new(42);
    private int _modeTicks;

    public Mutator(ProjectionStore store, DispatcherQueue dispatcherQueue)
    {
        _store = store;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();
    }

    public IMutationContext? Context { get; set; }

    public MutationMode Mode { get; private set; }

    public int Ticks { get; private set; }

    public int RecordsUpdated { get; private set; }

    public void Start(MutationMode mode)
    {
        Mode = mode;
        _modeTicks = 0;
        _timer.Start();
        SpikeLog.Write($"Mutator start mode={mode}");
    }

    public void Stop()
    {
        _timer.Stop();
        SpikeLog.Write($"Mutator stop mode={Mode} ticks={Ticks}");
        Mode = MutationMode.Stopped;
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"mode={Mode} ticks={Ticks} recordsUpdated={RecordsUpdated}");

    private void OnTick()
    {
        Ticks++;
        _modeTicks++;
        var context = Context;
        context?.Sample(Ticks, Mode);

        var plan = new Dictionary<Guid, ItemRecord>();
        var notes = new List<string>();
        var items = context?.Items ?? [];
        var focused = context?.FocusedItem;

        if (items.Count == 0)
        {
            for (var i = 0; i < 5; i++)
            {
                RetitleByNumber(plan, _random.Next(1, _store.Count + 1));
            }
        }
        else
        {
            var anchorItem = focused ?? context?.SelectedItem;
            var anchor = anchorItem is null ? Math.Min(20, items.Count - 1) : IndexOf(items, anchorItem);
            anchor = Math.Max(anchor, 0);

            for (var offset = -3; offset <= 3; offset++)
            {
                var index = anchor + offset;
                if (offset != 0 && index >= 0 && index < items.Count && !ReferenceEquals(items[index], focused))
                {
                    Retitle(plan, items[index].Id, "rev");
                }
            }

            for (var i = 0; i < 2; i++)
            {
                var other = items[_random.Next(items.Count)];
                if (!ReferenceEquals(other, focused))
                {
                    Retitle(plan, other.Id, "rev");
                }
            }

            if (Mode == MutationMode.OtherRows)
            {
                if (focused is not null && _modeTicks % 10 == 0)
                {
                    Retitle(plan, focused.Id, "STORE rev");
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"same-row title update for focused {focused.Number}"));
                }

                // The two kinds of unrelated re-sort run on different ticks so their effects can be told apart.
                if (_modeTicks % 10 == 5)
                {
                    MoveNeighbourBelowToEnd(plan, notes, items, anchor, focused);
                }
                else if (_modeTicks % 10 == 0)
                {
                    MoveFarRowAboveAnchor(plan, notes, items, anchor, focused);
                }
            }
            else if (Mode == MutationMode.MoveFocused && focused is not null)
            {
                MoveFocused(plan, notes, items, anchor, focused);
            }
        }

        RecordsUpdated += plan.Count;
        if (notes.Count > 0)
        {
            SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"tick={Ticks} {string.Join("; ", notes)}"));
        }

        _store.Apply([.. plan.Values]);
    }

    private static int IndexOf(IReadOnlyList<ItemViewModel> items, ItemViewModel target)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private void MoveNeighbourBelowToEnd(Dictionary<Guid, ItemRecord> plan, List<string> notes, IReadOnlyList<ItemViewModel> items, int anchor, ItemViewModel? focused)
    {
        // A visible neighbour below the anchor row leaves the viewport for the end of the list.
        if (anchor + 2 < items.Count && !ReferenceEquals(items[anchor + 2], focused))
        {
            var leaving = items[anchor + 2];
            SetRank(plan, leaving.Id, _store.NextEndRank());
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"re-sort: {leaving.Number} moves from index {anchor + 2} to end (below anchor)"));
        }
    }

    private void MoveFarRowAboveAnchor(Dictionary<Guid, ItemRecord> plan, List<string> notes, IReadOnlyList<ItemViewModel> items, int anchor, ItemViewModel? focused)
    {
        // A far row is inserted just above the anchor row (between anchor-2 and anchor-1).
        if (anchor >= 2)
        {
            var arriving = items[items.Count - 1];
            if (!ReferenceEquals(arriving, focused) && !plan.ContainsKey(arriving.Id))
            {
                var rank = items[anchor - 2].Rank + ((items[anchor - 1].Rank - items[anchor - 2].Rank) / 2);
                SetRank(plan, arriving.Id, rank);
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"re-sort: {arriving.Number} moves from end to above anchor (rank {rank})"));
            }
        }
    }

    private void MoveFocused(Dictionary<Guid, ItemRecord> plan, List<string> notes, IReadOnlyList<ItemViewModel> items, int anchor, ItemViewModel focused)
    {
        int? rank = _modeTicks switch
        {
            15 when anchor + 3 < items.Count => items[anchor + 3].Rank + 1,
            25 when anchor >= 3 => items[anchor - 3].Rank - 1,
            35 => _store.NextEndRank(),
            _ => null,
        };

        if (rank is int newRank)
        {
            SetRank(plan, focused.Id, newRank);
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"move-focused: {focused.Number} from index {anchor} to rank {newRank} (mode tick {_modeTicks})"));
        }
    }

    private void RetitleByNumber(Dictionary<Guid, ItemRecord> plan, int number) =>
        Retitle(plan, ItemRecord.KeyFor(number), "rev");

    private void Retitle(Dictionary<Guid, ItemRecord> plan, Guid id, string label)
    {
        var current = Current(plan, id);
        if (current is not null)
        {
            plan[id] = current with { Title = string.Create(CultureInfo.InvariantCulture, $"Item {current.Number:D5} · {label} {Ticks}") };
        }
    }

    private void SetRank(Dictionary<Guid, ItemRecord> plan, Guid id, int rank)
    {
        var current = Current(plan, id);
        if (current is not null)
        {
            plan[id] = current with { Rank = rank };
        }
    }

    private ItemRecord? Current(Dictionary<Guid, ItemRecord> plan, Guid id) =>
        plan.TryGetValue(id, out var planned) ? planned : _store.Find(id);
}
