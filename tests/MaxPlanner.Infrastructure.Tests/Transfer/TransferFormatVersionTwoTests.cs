using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using MaxPlanner.Application.Common;
using MaxPlanner.Application.Transfer;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Infrastructure.Tests.Persistence;
using MaxPlanner.Infrastructure.Transfer;
using static MaxPlanner.Infrastructure.Tests.Persistence.TestDataset;
using static MaxPlanner.Infrastructure.Tests.Transfer.TestWorkbook;

namespace MaxPlanner.Infrastructure.Tests.Transfer;

/// <summary>
/// ADR 0028: format version 2 writes one Designer and one Developer ID per task and readable name columns in the Tasks
/// table; version 1 files, with their arrays, still import (first person kept, the others dropped with a warning).
/// </summary>
[Trait("Req", "B-12")]
[Trait("Req", "B-02")]
public sealed class TransferFormatVersionTwoTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TempFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task A_json_export_writes_version_2_with_single_ids_and_imports_into_an_empty_dataset_unchanged()
    {
        await using var source = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(source);
        var path = await ExportAsync(source, TransferFileFormat.Json, null, "full.json");

        using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, Ct)))
        {
            json.RootElement.GetProperty("formatVersion").GetInt32().Should().Be(2);
            var tasks = json.RootElement.GetProperty("tasks").EnumerateArray().ToList();
            tasks.Should().AllSatisfy(t => (t.TryGetProperty("designerIds", out _) || t.TryGetProperty("developerIds", out _)).Should().BeFalse());
            var linked = tasks.Single(t => t.GetProperty("id").GetGuid() == fixture.LinkedOwnTitle.Value);
            (linked.GetProperty("designerId").GetGuid(), linked.GetProperty("developerId").GetGuid()).Should().Be((fixture.Alice.Value, fixture.Bob.Value));
        }

        await using var target = await CreateAsync();
        var plan = await Transfers.PreviewAsync(target, path);
        plan.Findings.Should().NotContain(f => f.Severity == ImportSeverity.Error || f.Severity == ImportSeverity.Conflict || f.Code == ImportFindingCodes.PeopleDropped);
        await Transfers.ApplyAsync(target, plan);

        TransferFixture.Shape(await target.SnapshotAsync()).Should().BeEquivalentTo(TransferFixture.Shape(await source.SnapshotAsync()), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task A_csv_tasks_table_names_the_designer_developer_and_tags_and_merges_back_as_no_change()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = await ExportAsync(db, TransferFileFormat.CsvTable, TransferTable.Tasks, "tasks.csv");

        var table = CsvTable.Read("tasks.csv", CsvTable.Decode(await File.ReadAllBytesAsync(path, Ct), "tasks.csv"), ',', Ct);
        var headers = table.Headers.ToList();
        headers.Should().ContainInConsecutiveOrder("Priority", "DesignerId", "Designer", "DeveloperId", "Developer", "TagIds", "Tags", "Notes");
        headers.Should().NotContain(["DesignerIds", "DeveloperIds"]);
        var row = table.Rows.Single(r => r[0] == fixture.LinkedOwnTitle.Value.ToString("D"));
        string? Cell(string column) => row[headers.IndexOf(column)];
        (Cell("DesignerId"), Cell("Designer"), Cell("DeveloperId"), Cell("Developer"), Cell("Tags"))
            .Should().Be((fixture.Alice.Value.ToString("D"), "Alice", fixture.Bob.Value.ToString("D"), "Bob", "Mobile"));

        // A single table carries no field definitions, so its lookup option IDs cannot be mapped; this test is about
        // the people and tag columns, so the custom values column is left out of the merge.
        var read = await ReadTasksCsvAsync(path);
        read = read with { Tasks = [.. read.Tasks.Select(t => t with { SuppliedFields = t.SuppliedFields!.Where(f => f != "CustomValues").ToHashSet() })] };
        var merge = await PreviewAsync(db, read, ImportMode.Merge);

        merge.Findings.Where(f => f.Severity == ImportSeverity.Error).Should().BeEmpty();
        merge.RecordWriteCount.Should().Be(0, "the name columns are informational and the IDs are unchanged");
    }

    [Fact]
    public async Task A_csv_bundle_round_trip_keeps_the_designer_and_developer()
    {
        await using var source = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(source);
        var path = await ExportAsync(source, TransferFileFormat.CsvBundle, null, "bundle.zip");
        await using var target = await CreateAsync();

        await Transfers.ApplyAsync(target, await Transfers.PreviewAsync(target, path));

        var task = (await target.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        (task.DesignerId, task.DeveloperId).Should().Be((fixture.Alice, fixture.Bob));
    }

    [Fact]
    public async Task A_partial_csv_with_only_a_developer_id_column_changes_the_developer_and_keeps_the_designer()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = await WriteTextAsync("partial.csv", $"Id,DeveloperId\r\n{fixture.LinkedOwnTitle.Value:D},{fixture.Alice.Value:D}\r\n");

        var plan = await PreviewAsync(db, await ReadTasksCsvAsync(path), ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        (task.DesignerId, task.DeveloperId).Should().Be((fixture.Alice, fixture.Alice));
    }

    [Fact]
    public async Task A_version_1_csv_with_legacy_array_columns_still_merges_keeping_the_first_person_with_a_warning()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = await WriteTextAsync(
            "legacy.csv",
            $"Id,DesignerIds,DeveloperIds\r\n{fixture.LinkedOwnTitle.Value:D},\"[\"\"{fixture.Bob.Value:D}\"\",\"\"{fixture.Alice.Value:D}\"\"]\",[]\r\n");

        var plan = await PreviewAsync(db, await ReadTasksCsvAsync(path), ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Match<ImportFinding>(f => f.Severity == ImportSeverity.Warning && f.Field == "DesignerIds" && f.Detail == "Alice");
        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        (task.DesignerId, task.DeveloperId).Should().Be((fixture.Bob, (PersonId?)null), "an empty legacy array clears the role");
    }

    [Fact]
    public async Task A_version_1_json_file_with_designer_and_developer_arrays_still_imports()
    {
        await using var target = await CreateAsync();
        var alice = target.AddPerson("Alice");
        var bob = target.AddPerson("Bob");
        var id = Guid.NewGuid();
        var path = await WriteTextAsync("v1.json", $$"""
            { "format": "max-planner-transfer", "formatVersion": 1,
              "lookups": { "people": [ { "id": "{{alice.Value}}", "name": "Alice" }, { "id": "{{bob.Value}}", "name": "Bob" } ] },
              "tasks": [ { "id": "{{id}}", "ownTitle": "From version 1", "designerIds": [ "{{alice.Value}}", "{{bob.Value}}" ], "developerIds": [ "{{bob.Value}}" ], "tagIds": [] } ] }
            """);

        var plan = await Transfers.PreviewAsync(target, path);
        await Transfers.ApplyAsync(target, plan);

        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Be(new ImportFinding(ImportSeverity.Warning, ImportEntity.Task, id.ToString(), ImportFindingCodes.PeopleDropped, "DesignerIds", "Bob"));
        var task = (await target.SnapshotAsync()).Tasks.Single(t => t.Id.Value == id);
        (task.DesignerId, task.DeveloperId).Should().Be((alice, bob));
    }

    [Fact]
    public async Task A_version_1_workbook_with_legacy_array_columns_still_imports()
    {
        await using var target = await CreateAsync();
        var alice = target.AddPerson("Alice");
        var bob = target.AddPerson("Bob");
        var id = Guid.NewGuid();
        var path = _folder.Write("v1.xlsx", Create(
        [
            Metadata("1"),
            Sheet("Tasks", Header("Id", "OwnTitle", "DesignerIds", "DeveloperIds"),
                Row(Text(id.ToString()), Text("From version 1"), Text($"[\"{alice.Value}\"]"), Text($"[\"{bob.Value}\",\"{alice.Value}\"]"))),
        ]));

        var plan = await Transfers.PreviewAsync(target, path);
        await Transfers.ApplyAsync(target, plan);

        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Match<ImportFinding>(f => f.Field == "DeveloperIds" && f.Detail == "Alice");
        var task = (await target.SnapshotAsync()).Tasks.Single(t => t.Id.Value == id);
        (task.DesignerId, task.DeveloperId).Should().Be((alice, bob));
    }

    [Fact]
    public async Task A_supplied_but_blank_legacy_designer_column_clears_the_existing_designer_on_merge()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = await WriteTextAsync("blank-legacy.csv", $"Id,DesignerIds\r\n{fixture.LinkedOwnTitle.Value:D},\r\n");

        await Transfers.ApplyAsync(db, await PreviewAsync(db, await ReadTasksCsvAsync(path), ImportMode.Merge));

        var task = (await db.SnapshotAsync()).Tasks.Single(t => t.Id == fixture.LinkedOwnTitle);
        (task.DesignerId, task.DeveloperId).Should().Be(((PersonId?)null, fixture.Bob), "only the Designer column was supplied");
    }

    [Fact]
    public async Task With_both_designer_columns_a_blank_array_uses_the_single_id_and_a_filled_array_wins()
    {
        await using var db = await CreateAsync();
        var fixture = await TransferFixture.PopulateAsync(db);
        var path = await WriteTextAsync(
            "both.csv",
            $"Id,DesignerIds,DesignerId\r\n"
            + $"{fixture.LinkedOwnTitle.Value:D},,{fixture.Bob.Value:D}\r\n"
            + $"{fixture.Unlinked.Value:D},\"[\"\"{fixture.Bob.Value:D}\"\",\"\"{fixture.Alice.Value:D}\"\"]\",{fixture.Alice.Value:D}\r\n");

        var plan = await PreviewAsync(db, await ReadTasksCsvAsync(path), ImportMode.Merge);
        await Transfers.ApplyAsync(db, plan);

        var tasks = (await db.SnapshotAsync()).Tasks;
        tasks.Single(t => t.Id == fixture.LinkedOwnTitle).DesignerId.Should().Be(fixture.Bob, "a blank legacy cell falls back to DesignerId");
        tasks.Single(t => t.Id == fixture.Unlinked).DesignerId.Should().Be(fixture.Bob, "a filled legacy array takes precedence");
        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Match<ImportFinding>(f => f.RecordKey == fixture.Unlinked.Value.ToString() && f.Field == "DesignerIds" && f.Detail == "Alice");
    }

    [Fact]
    public async Task A_version_1_csv_bundle_with_legacy_array_columns_still_imports()
    {
        await using var target = await CreateAsync();
        var alice = target.AddPerson("Alice");
        var bob = target.AddPerson("Bob");
        var id = Guid.NewGuid();
        var path = _folder.Write("v1.zip", Zip(
            (TransferFileWriter.BundleMetadataEntry, $$"""
                { "format": "max-planner-transfer", "formatVersion": 1,
                  "lookups": { "people": [ { "id": "{{alice.Value}}", "name": "Alice" }, { "id": "{{bob.Value}}", "name": "Bob" } ] } }
                """),
            ("Tasks.csv", $"Id,OwnTitle,DesignerIds,DeveloperIds\r\n{id:D},From version 1,\"[\"\"{bob.Value:D}\"\",\"\"{alice.Value:D}\"\"]\",\"[\"\"{alice.Value:D}\"\"]\"\r\n")));

        var plan = await Transfers.PreviewAsync(target, path);
        await Transfers.ApplyAsync(target, plan);

        plan.Findings.Should().ContainSingle(f => f.Code == ImportFindingCodes.PeopleDropped)
            .Which.Should().Be(new ImportFinding(ImportSeverity.Warning, ImportEntity.Task, id.ToString(), ImportFindingCodes.PeopleDropped, "DesignerIds", "Alice"));
        var task = (await target.SnapshotAsync()).Tasks.Single(t => t.Id.Value == id);
        (task.DesignerId, task.DeveloperId).Should().Be((bob, alice));
    }

    [Fact]
    public async Task A_json_file_of_format_version_3_is_rejected()
    {
        var path = await WriteTextAsync("v3.json", """{ "format": "max-planner-transfer", "formatVersion": 3 }""");

        (await Transfers.ReadErrorAsync(path)).Should().Be(new TransferFileError(TransferFailure.UnsupportedVersion, "3"));
    }

    private async Task<string> ExportAsync(TestDataset db, TransferFileFormat format, TransferTable? table, string name)
    {
        var path = _folder.File(name);
        var result = await Transfers.Exporter(db).ExportAsync(EntireDatasetScope.Instance, format, table, path, Ct);
        result.Error.Should().BeNull();
        return path;
    }

    private async Task<string> WriteTextAsync(string name, string text)
    {
        var path = _folder.File(name);
        await File.WriteAllTextAsync(path, text, Utf8, Ct);
        return path;
    }

    private static async Task<TransferDocument> ReadTasksCsvAsync(string path)
    {
        var result = await TransferAdapters.CreateReader().ReadAsync(path, TransferTable.Tasks, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }

    private static async Task<ImportPlan> PreviewAsync(TestDataset db, TransferDocument document, ImportMode mode)
    {
        var result = await Transfers.Importer(db).PreviewAsync(document, mode, Ct);
        result.Error.Should().BeNull();
        return result.Value;
    }
}
