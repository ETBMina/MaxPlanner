using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Presentation.Common.Options;

/// <summary>A squad in the CR editor's Squad box; a null ID is "General (no squad)" (D15).</summary>
public sealed record SquadOption(SquadId? Id, string Name)
{
    public override string ToString() => Name;
}
