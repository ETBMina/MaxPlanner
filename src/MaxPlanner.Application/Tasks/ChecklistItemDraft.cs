using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Policies;
using MaxPlanner.Domain.Tasks;
using MaxPlanner.Domain.Values;

namespace MaxPlanner.Application.Tasks;

/// <summary>
/// A checklist item as an editor draft holds it, in display order (B-02.7). Existing items keep their ID
/// through renames and reordering; a new item carries a preallocated ID.
/// </summary>
public sealed record ChecklistItemDraft(ChecklistItemId Id, string? Text, bool IsCompleted);

internal static class ChecklistDrafts
{
    /// <summary>Validates every item's text; blank text is an error, never a deletion (B-02.7, B-05).</summary>
    /// <returns>The parsed texts in draft order, or null when any text is invalid.</returns>
    public static IReadOnlyList<(ChecklistItemId Id, ChecklistText Text, bool IsCompleted)>? Parse(
        IReadOnlyList<ChecklistItemDraft>? drafts,
        ValidationCollector validation)
    {
        var parsed = new List<(ChecklistItemId, ChecklistText, bool)>();
        var isValid = true;
        foreach (var draft in drafts ?? [])
        {
            var text = ChecklistText.Create(draft.Text);
            if (text.Error is not null)
            {
                // One error per field is enough for the editor; it marks the offending rows itself.
                if (isValid)
                {
                    validation.Add(text.Error with { Field = "ChecklistItems" });
                }

                isValid = false;
                continue;
            }

            parsed.Add((draft.Id, text.Value, draft.IsCompleted));
        }

        return isValid ? parsed : null;
    }

    /// <summary>
    /// The final checklist with ranks in draft order. Items not on <paramref name="existing"/> are new and
    /// must use unused IDs; a deleted item's ID cannot come back (B-05). Items left out of the draft are
    /// deleted and tombstoned.
    /// </summary>
    public static bool TryBuild(
        IWriteTransaction tx,
        IReadOnlyList<ChecklistItem> existing,
        IReadOnlyList<(ChecklistItemId Id, ChecklistText Text, bool IsCompleted)> parsed,
        out IReadOnlyList<ChecklistItem> items,
        out AppError? error)
    {
        var known = existing.Select(i => i.Id).ToHashSet();
        var result = new List<ChecklistItem>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var (id, text, isCompleted) = parsed[i];
            if (!known.Contains(id) && TransactionGuards.EnsureNewId(tx, EntityKind.ChecklistItem, id.Value, exists: false) is { } idError)
            {
                items = [];
                error = idError;
                return false;
            }

            result.Add(new ChecklistItem(id, text, isCompleted, (i + 1) * RankPolicy.Step));
        }

        foreach (var removed in existing.Where(e => parsed.All(p => p.Id != e.Id)))
        {
            TransactionGuards.Tombstone(tx, EntityKind.ChecklistItem, removed.Id.Value);
        }

        items = result;
        error = null;
        return true;
    }

    /// <summary>The draft's checklist for a new task (no existing items).</summary>
    public static bool TryBuildNew(
        IWriteTransaction tx,
        IReadOnlyList<(ChecklistItemId Id, ChecklistText Text, bool IsCompleted)> parsed,
        out IReadOnlyList<ChecklistItem> items,
        out AppError? error) => TryBuild(tx, [], parsed, out items, out error);
}
