using MaxPlanner.Application.Abstractions;

namespace MaxPlanner.Application.Settings;

/// <summary>The workflow, field, and defaults management use cases of Settings (B-04, B-11) over one dataset.</summary>
public sealed record SettingsUseCases(
    AddLookup AddLookup,
    RenameLookup RenameLookup,
    MoveLookup MoveLookup,
    SetTaskStatusFlags SetTaskStatusFlags,
    SetCrStatusFlags SetCrStatusFlags,
    PreviewLookupRemoval PreviewLookupRemoval,
    RemoveLookup RemoveLookup,
    UpdateWorkflowDefaults UpdateWorkflowDefaults,
    AddFieldDefinition AddFieldDefinition,
    UpdateFieldDefinition UpdateFieldDefinition,
    MoveFieldDefinition MoveFieldDefinition,
    DeactivateFieldDefinition DeactivateFieldDefinition)
{
    public static SettingsUseCases Create(IMutationCoordinator coordinator, IReadSnapshotSource snapshots)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(snapshots);
        return new(
            new AddLookup(coordinator),
            new RenameLookup(coordinator),
            new MoveLookup(coordinator),
            new SetTaskStatusFlags(coordinator),
            new SetCrStatusFlags(coordinator),
            new PreviewLookupRemoval(snapshots),
            new RemoveLookup(coordinator),
            new UpdateWorkflowDefaults(coordinator),
            new AddFieldDefinition(coordinator),
            new UpdateFieldDefinition(coordinator),
            new MoveFieldDefinition(coordinator),
            new DeactivateFieldDefinition(coordinator));
    }
}
