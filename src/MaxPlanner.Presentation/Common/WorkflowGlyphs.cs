using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Presentation.Common;

/// <summary>The Segoe Fluent Icons glyphs for priorities (design §6.1) and CR statuses (design §6.2; task statuses show no glyph), shared by every surface.</summary>
public static class WorkflowGlyphs
{
    /// <summary>Important, ChevronUp, Remove (dash), ChevronDown (design §6.1).</summary>
    public static string Priority(PriorityLevel level) => level switch
    {
        PriorityLevel.Level1 => "",
        PriorityLevel.Level2 => "",
        PriorityLevel.Lowest => "",
        _ => "",
    };

    /// <summary>CR statuses have no "sets start date" flag: Completed, CircleRing (backlog), or none.</summary>
    public static string CrStatus(CrStatusDefinition status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsCompleted ? "" : status.IsBacklog ? "" : string.Empty;
    }
}
