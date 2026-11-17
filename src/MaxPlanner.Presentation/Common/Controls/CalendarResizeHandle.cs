using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// A bar edge handle (B-08.8): a <see cref="Thumb"/> for the drag inside a panel that shows the horizontal resize
/// cursor (Thumb is sealed, so the cursor is set on this host).
/// </summary>
public sealed partial class CalendarResizeHandle : WinGrid
{
    public CalendarResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Width = 8;
        Thumb = new Thumb
        {
            IsTabStop = false,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Children.Add(Thumb);
    }

    public Thumb Thumb { get; }
}
