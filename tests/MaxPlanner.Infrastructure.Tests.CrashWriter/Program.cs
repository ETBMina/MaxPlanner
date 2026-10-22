using System.Globalization;
using MaxPlanner.Infrastructure.Persistence;

// Commits tasks forever, printing each commit sequence only after Commit() returns.
// The parent test kills this process and checks that every printed (acknowledged) commit survived.
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: CrashWriter <database file>");
    return 2;
}

using var writer = new SqliteConnectionFactory(args[0]).OpenWriter();
for (long sequence = 1; ; sequence++)
{
    using (var transaction = writer.BeginTransaction(deferred: false))
    {
        using var insert = writer.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tasks (id, own_title, revision) VALUES ($id, 'Task', 1);
            UPDATE app_metadata SET commit_sequence = commit_sequence + 1 WHERE id = 1;
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid());
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    Console.Out.WriteLine(sequence.ToString(CultureInfo.InvariantCulture));
    Console.Out.Flush();
}
