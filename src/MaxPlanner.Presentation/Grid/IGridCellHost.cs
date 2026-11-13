using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MaxPlanner.Presentation.Grid;

/// <summary>What the Grid's cells, rows, and headers ask of the page that hosts them. Implemented by the Grid page view.</summary>
internal interface IGridCellHost
{
    /// <summary>Raised when the displayed columns, their order, or the frozen-column offset changed.</summary>
    event EventHandler? DisplayLayoutChanged;

    GridPageViewModel? GridViewModel { get; }

    GridTable? Table { get; }

    IReadOnlyList<GridHeaderCell> HeaderCells { get; }

    /// <summary>The visible columns in display order (at S the Title column comes first and is frozen, design §4.3).</summary>
    IReadOnlyList<GridColumnViewModel> DisplayColumns { get; }

    /// <summary>The horizontal offset applied to the frozen first column so it stays in view; 0 when none is frozen.</summary>
    double FrozenOffset { get; }

    bool IsFirstColumnFrozen { get; }

    void OnCellKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellPreviewKeyDown(GridCell cell, KeyRoutedEventArgs e);

    void OnCellCharacterReceived(GridCell cell, CharacterReceivedRoutedEventArgs e);

    void OnCellDoubleTapped(GridCell cell);

    /// <summary>A single tap on a cell: choice and flyout cells open their editor at once (design §4.3); other cells ignore it.</summary>
    void OnCellTapped(GridCell cell);

    /// <summary>UIA Invoke on a cell: opens its editor, like F2.</summary>
    void BeginEditFromAutomation(GridCell cell);

    /// <summary>UIA ValuePattern.SetValue on a cell: edits and commits through the same command path as typing.</summary>
    void SetValueFromAutomation(GridCell cell, string value);

    /// <summary>An inline cell editor lost focus: B-07 leaving an edited cell attempts a commit.</summary>
    void OnEditorLostFocus(Control editor);

    /// <summary>The row ⋯ button (design §4.3 row menu).</summary>
    void OnRowMenuRequested(GridRowViewModel row, FrameworkElement anchor);
}
