using MaxPlanner.Domain.Identifiers;

namespace MaxPlanner.Presentation.Common.Pickers;

/// <summary>A CR row in the link picker (design §5.6): number, title, owner, and linked-task count. Never "occupied".</summary>
public sealed record CrChoice(ChangeRequestId Id, string Number, string Title, string OwnerName, string SquadName, string? TaskNumber, int LinkedTaskCount, long Revision, string Secondary)
{
    public override string ToString() => Number + " · " + Title;
}
