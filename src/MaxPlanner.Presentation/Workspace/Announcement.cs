
namespace MaxPlanner.Presentation.Workspace;

/// <summary>A screen-reader announcement (design §8.3).</summary>
/// <param name="IsFailure">ActionAborted rather than ActionCompleted.</param>
public sealed record Announcement(string Text, bool IsFailure);
