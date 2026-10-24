using Windows.UI.ViewManagement;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// Design §9: Windows "Animation effects". Scrolls that the app starts itself (column picker, bring into view) run
/// without animation when it is off; platform theme transitions already follow the setting on their own.
/// </summary>
public static class MotionSettings
{
    private static readonly Lazy<UISettings> Settings = new(() => new UISettings());

    /// <summary>Read on every call, so a change in Windows settings applies to the next scroll.</summary>
    public static bool AnimationsEnabled => Settings.Value.AnimationsEnabled;
}
