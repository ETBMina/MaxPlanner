namespace MaxPlanner.Presentation.Board;

/// <summary>The items of the Board card menu (design §4.2), in menu order; <see cref="Separator"/> is a divider line.</summary>
internal enum BoardCardMenuItem
{
    Open,
    OpenParent,
    OpenCr,
    MoveTo,
    MoveUp,
    MoveDown,
    Separator,
    Copy,
    CopyId,
    AddSubtaskCard,
    Archive,
    CreatePullRequest,
    Delete,
}

/// <summary>Which items the Board card menu shows (design §4.2, §5.11). WinUI-free so the decision has a test.</summary>
internal static class BoardCardMenuRules
{
    /// <param name="isTask">A task card; false for a Subtask card.</param>
    /// <param name="hasCrReference">The card shows a CR link; the menu is its keyboard route (design §7.3).</param>
    /// <param name="isPullRequestAvailable">Settings › Integrations › Azure DevOps is on.</param>
    /// <remarks>
    /// Open parent is for Subtask cards; Copy…, Add Subtask card and Archive for tasks. Create pull request… sits just above
    /// Delete…, separated from it, for both kinds while the integration is on.
    /// </remarks>
    public static IReadOnlyList<BoardCardMenuItem> ItemsFor(bool isTask, bool hasCrReference, bool isPullRequestAvailable)
    {
        var items = new List<BoardCardMenuItem> { BoardCardMenuItem.Open };
        if (!isTask)
        {
            items.Add(BoardCardMenuItem.OpenParent);
        }

        if (hasCrReference)
        {
            items.Add(BoardCardMenuItem.OpenCr);
        }

        items.AddRange([BoardCardMenuItem.MoveTo, BoardCardMenuItem.MoveUp, BoardCardMenuItem.MoveDown, BoardCardMenuItem.Separator]);
        if (isTask)
        {
            items.Add(BoardCardMenuItem.Copy);
        }

        items.Add(BoardCardMenuItem.CopyId);
        if (isTask)
        {
            items.Add(BoardCardMenuItem.AddSubtaskCard);
            items.Add(BoardCardMenuItem.Archive);
        }

        if (isPullRequestAvailable)
        {
            items.Add(BoardCardMenuItem.CreatePullRequest);
        }

        items.Add(BoardCardMenuItem.Separator);
        items.Add(BoardCardMenuItem.Delete);
        return items;
    }
}
