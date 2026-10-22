using System.Runtime.CompilerServices;
using WinUI.TableView;

namespace GridGate;

/// <summary>
/// ADR 0004 gate evidence: WinUI.TableView 1.5.1 has no public commit/cancel-edit API, so an edit
/// that was kept open by cancelling CellEditEnding cannot be closed after an asynchronous save
/// completes. These accessors bind to internal members by name (verified against 1.5.1 only) and
/// throw MissingMethodException if a package update renames them.
/// </summary>
internal static class TableViewInternals
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EndCellEditing")]
    public static extern bool EndCellEditing(TableView tableView, TableViewEditAction editAction, TableViewCell cell);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetIsEditing")]
    public static extern void SetIsEditing(TableView tableView, bool value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_IsEditing")]
    public static extern bool GetIsEditing(TableView tableView);
}
