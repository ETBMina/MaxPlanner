using MaxPlanner.Application.Preferences;

namespace MaxPlanner.Presentation.Shell;

/// <param name="Bounds">Restored-window bounds in physical desktop pixels, fully inside one work area.</param>
/// <param name="Maximize">Maximize after placing the restored bounds, so un-maximizing returns to them.</param>
public sealed record WindowPlacementDecision(PixelRect Bounds, bool Maximize);

/// <summary>
/// Chooses where the main window opens (design §2.1): the saved bounds fitted to a display that exists now,
/// or the first-run size centered on the primary work area.
/// </summary>
public static class WindowPlacementPolicy
{
    /// <param name="workAreas">The current displays' work areas.</param>
    /// <param name="defaultSize">First-run size, already converted to physical pixels.</param>
    /// <param name="minimumSize">The window's minimum size in physical pixels.</param>
    public static WindowPlacementDecision Resolve(
        WindowPlacementPreference? saved,
        IReadOnlyList<PixelRect> workAreas,
        PixelRect primaryWorkArea,
        (int Width, int Height) defaultSize,
        (int Width, int Height) minimumSize)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (saved is null)
        {
            return new WindowPlacementDecision(Center(primaryWorkArea, Fit(defaultSize, primaryWorkArea, minimumSize)), Maximize: false);
        }

        // The same work area as when saved means the same monitor arrangement, so the bounds are kept;
        // otherwise the display that shows most of the saved window takes it, else the primary display.
        PixelRect target;
        if (saved.MonitorWorkArea is { } hint && workAreas.Contains(hint))
        {
            target = hint;
        }
        else
        {
            target = workAreas
                .Select(area => (Area: area, Overlap: IntersectionArea(area, saved.Bounds)))
                .Where(candidate => candidate.Overlap > 0)
                .OrderByDescending(candidate => candidate.Overlap)
                .Select(candidate => candidate.Area)
                .FirstOrDefault(primaryWorkArea);
        }

        var size = Fit((saved.Bounds.Width, saved.Bounds.Height), target, minimumSize);
        var bounds = IntersectionArea(target, saved.Bounds) > 0
            ? new PixelRect(
                Math.Clamp(saved.Bounds.X, target.X, target.X + target.Width - size.Width),
                Math.Clamp(saved.Bounds.Y, target.Y, target.Y + target.Height - size.Height),
                size.Width,
                size.Height)
            : Center(target, size);
        return new WindowPlacementDecision(bounds, saved.State == WindowStatePreference.Maximized);
    }

    private static (int Width, int Height) Fit((int Width, int Height) size, PixelRect area, (int Width, int Height) minimum) =>
        (Math.Clamp(size.Width, Math.Min(minimum.Width, area.Width), area.Width),
         Math.Clamp(size.Height, Math.Min(minimum.Height, area.Height), area.Height));

    private static PixelRect Center(PixelRect area, (int Width, int Height) size) =>
        new(area.X + ((area.Width - size.Width) / 2), area.Y + ((area.Height - size.Height) / 2), size.Width, size.Height);

    private static long IntersectionArea(PixelRect a, PixelRect b)
    {
        long width = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        long height = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }
}
