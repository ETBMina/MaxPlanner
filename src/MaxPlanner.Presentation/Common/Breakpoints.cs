namespace MaxPlanner.Presentation.Common;

/// <summary>
/// The window-width breakpoints of design §2.2: S below <see cref="Medium"/>, M from <see cref="Medium"/>, L from
/// <see cref="Large"/> (the NavigationView's LeftMinimal, LeftCompact and Left).
/// </summary>
public static class Breakpoints
{
    /// <summary>The smallest M width, in epx.</summary>
    public const double Medium = 640;

    /// <summary>The smallest L width, in epx.</summary>
    public const double Large = 1008;

    /// <summary>The value for <paramref name="width"/>'s size class: L, M, or S.</summary>
    public static T BySize<T>(double width, T large, T medium, T small) =>
        width >= Large ? large : width >= Medium ? medium : small;
}
