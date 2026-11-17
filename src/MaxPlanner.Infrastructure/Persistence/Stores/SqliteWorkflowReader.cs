using MaxPlanner.Application.Abstractions;
using MaxPlanner.Application.Workflow;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

internal sealed class SqliteWorkflowReader(SqliteWriteTransaction tx) : IWorkflowReader
{
    public WorkflowSnapshot Read() => tx.Loader.LoadWorkflow();
}
