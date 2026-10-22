namespace MaxPlanner.Domain.Identifiers;

// T-04/T-05: identities are stable Guids. Distinct types stop a task ID from being
// passed where a CR ID is expected; labels, numbers, and positions are never identities.

public readonly record struct TaskId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct SubtaskCardId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct ChecklistItemId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct ChangeRequestId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct ReviewItemId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct GeneralTodoId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct TaskStatusId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct CrStatusId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct PriorityId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct SquadId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct PersonId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct TagId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct FieldId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct DatasetId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

public readonly record struct FieldOptionId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
