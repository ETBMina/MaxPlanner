namespace MaxPlanner.Presentation.Common;

/// <summary>A priority choice shown with its glyph: a configured priority has a level; an "All priorities" choice has none.</summary>
public interface IPriorityChoice
{
    string DisplayName { get; }

    PriorityLevel? Level { get; }
}
