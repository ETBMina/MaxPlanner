namespace GridGate;

public static class SampleData
{
    private static readonly string[] s_squads = ["Retail Cards", "Payments", "Lending", "Digital Channels"];
    private static readonly string[] s_owners = ["Avery Chen", "Jordan Patel", "Sam Rivera", "Noor Haddad", "Lee Okafor"];
    private static readonly string[] s_services = ["Payments API", "Card Service", "Ledger", "Notifications", "Onboarding"];
    private static readonly string[] s_verbs = ["Add", "Fix", "Refactor", "Document", "Migrate", "Validate"];
    private static readonly string[] s_nouns = ["card limits", "rounding", "audit log", "login flow", "fee rules", "statement export"];

    /// <summary>Deterministic dataset so screenshots and UIA dumps are reproducible.</summary>
    public static List<TaskRecord> Create(int count)
    {
#pragma warning disable CA5394 // Not security-sensitive: deterministic fixture data.
        var random = new Random(42);
        var rows = new List<TaskRecord>(count);
        var created = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            // Row 1 (index 0) is an unlinked task so the first visible Title cell is editable.
            var kind = i % 10 == 9 ? TaskKind.SubtaskCard : TaskKind.Task;
            var isLinked = i % 5 is 1 or 2 or 3;
            var crIndex = 1000 + (i / 3);
            DateOnly? start = random.Next(4) == 0 ? null : new DateOnly(2026, 1, 1).AddDays(random.Next(300));
            DateOnly? end = start?.AddDays(random.Next(1, 20));
            rows.Add(new TaskRecord(
                Id: new Guid(i + 1, 0, 0, new byte[8]),
                Kind: kind,
                CrNumber: isLinked ? $"CR-{crIndex}" : null,
                TaskNumber: isLinked ? $"TN-{crIndex % 97:D3}" : null,
                Squad: isLinked ? s_squads[crIndex % s_squads.Length] : "General",
                Owner: isLinked ? s_owners[crIndex % s_owners.Length] : string.Empty,
                Title: $"{s_verbs[random.Next(s_verbs.Length)]} {s_nouns[random.Next(s_nouns.Length)]} #{i + 1}",
                IsLinked: isLinked,
                Service: s_services[random.Next(s_services.Length)],
                Status: Lookups.Statuses[random.Next(4)],
                Priority: Lookups.Priorities[random.Next(Lookups.Priorities.Count)],
                StartDate: start,
                EndDate: end,
                Notes: i % 4 == 0 ? $"First line of notes for row {i + 1}\nSecond line" : string.Empty,
                Tags: i % 3 == 0 ? "backend, q3" : string.Empty,
                CreatedAtUtc: created.AddMinutes(i)));
        }
#pragma warning restore CA5394

        return rows;
    }
}
