using MaxPlanner.Application.Abstractions;
using MaxPlanner.Infrastructure.Persistence.Mapping;

namespace MaxPlanner.Infrastructure.Persistence.Stores;

/// <summary>The B-13.5 <c>import_ledger</c>: insert-only; the primary key refuses a second row for one source identity.</summary>
internal sealed class SqliteImportLedgerStore(SqliteWriteTransaction tx) : IImportLedgerStore
{
    public Guid? Find(string sourceNamespace, string entityKind, string sourceKey)
    {
        using var command = tx.Command("SELECT record_id FROM import_ledger WHERE namespace = $ns AND entity_kind = $kind AND source_key = $key;");
        command.Add("$ns", sourceNamespace);
        command.Add("$kind", entityKind);
        command.Add("$key", sourceKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetGuid(0, "import_ledger", "record_id") : null;
    }

    public void Insert(string sourceNamespace, string entityKind, string sourceKey, Guid recordId)
    {
        tx.BeforeWrite("import_ledger.insert");
        var command = tx.Command("INSERT INTO import_ledger (namespace, entity_kind, source_key, record_id) VALUES ($ns, $kind, $key, $record);");
        command.Add("$ns", sourceNamespace);
        command.Add("$kind", entityKind);
        command.Add("$key", sourceKey);
        command.Add("$record", recordId);
        tx.Execute(command);
    }
}
