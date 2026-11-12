using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Presentation.Common.Options;

/// <summary>A task status offered by the Grid's Status editor.</summary>
public sealed record StatusOption(TaskStatusId Id, string Name)
{
    public override string ToString() => Name;
}
