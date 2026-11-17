namespace MaxPlanner.Presentation.Common.Services;

/// <summary>Screen-reader announcements (design §8.3); the shell raises the UIA notification. Use on the UI thread.</summary>
public interface IAnnouncer
{
    /// <param name="isFailure">A failure is announced with higher importance.</param>
    void Announce(string text, bool isFailure = false);
}
