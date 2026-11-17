namespace MaxPlanner.Presentation.Board;

/// <summary>
/// Where the card body's ⋯ button and title go (design §4.2, U-06). The ID row, a gap, the "Moving" caption and the title
/// share one grid (rows 0–3; column 1 holds only the ⋯ button). The button ends the ID row, or the title row on a card
/// without an ID row (an unlinked Subtask card), so it never takes a line of its own. The title spans the button's
/// column unless the button is in its row, so a task title keeps the card's full width.
/// </summary>
internal static class BoardCardLayout
{
    /// <summary>The ID row's grid row.</summary>
    public const int IdRow = 0;

    /// <summary>The title's grid row.</summary>
    public const int TitleRow = 3;

    /// <summary>The space below the ID row (8 epx, the card's row spacing); none when the card has no ID row.</summary>
    public static double IdRowGap(bool hasIdRow) => hasIdRow ? 8 : 0;

    /// <summary>The ⋯ button's row: the ID row when there is one, otherwise the title row.</summary>
    public static int MenuRow(bool hasIdRow) => hasIdRow ? IdRow : TitleRow;

    /// <summary>The title spans the button's column (2) unless the button shares its row (1).</summary>
    public static int TitleColumnSpan(bool hasIdRow) => MenuRow(hasIdRow) == TitleRow ? 1 : 2;
}
