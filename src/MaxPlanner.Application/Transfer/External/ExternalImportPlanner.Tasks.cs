using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer.External;

public static partial class ExternalImportPlanner
{
    private sealed partial class Builder
    {
        // ----- Tasks -----
        private TransferTask? ReadTask(ExternalTable table, ExternalRow row, TableColumns map, List<TransferLink> links)
        {
            const string Entity = ImportEntity.Task;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            var sourceKey = SourceKey(row, columns);
            if (RecordId(Entity, sourceKey, displayKey) is not { } id)
            {
                return null;
            }

            var supplied = columns.Keys.Where(f => f is not (ExternalField.SourceId or ExternalField.CrNumber or ExternalField.Squad or ExternalField.Owner or ExternalField.TaskNumber or ExternalField.Title or ExternalField.Created or ExternalField.ChecklistItems))
                .Select(MemberOf).ToHashSet(StringComparer.Ordinal);
            var title = Text(row, columns, ExternalField.Title);
            var references = List(Cell(row, columns, ExternalField.CrNumber)).Select(n => n.Trim()).DistinctBy(Norm).ToList();
            string? chosenNumber = null;
            var unselected = new List<string>();
            if (references.Count == 1)
            {
                chosenNumber = references[0];
            }
            else if (references.Count > 1)
            {
                var hasChoice = options.CrReferenceChoices.TryGetValue(sourceKey, out var choice);
                chosenNumber = hasChoice && choice is not null && references.Any(r => Norm(r) == Norm(choice)) ? references.First(r => Norm(r) == Norm(choice!)) : null;
                _choices.Add(new ExternalCrChoice(sourceKey, displayKey, references, chosenNumber, hasChoice));
                if (!hasChoice)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.MultipleCrReferences, "CrNumber"));
                }

                // B-13.6: the references not chosen are kept in legacy metadata.
                unselected.AddRange(references.Where(r => chosenNumber is null || Norm(r) != Norm(chosenNumber)));
            }

            var taskNumber = Text(row, columns, ExternalField.TaskNumber);
            var squad = Text(row, columns, ExternalField.Squad);
            var owner = Text(row, columns, ExternalField.Owner);
            string? preLinkTitle = null;
            string? ownTitle = title;
            var legacyBody = (string?)null;
            var notes = Text(row, columns, ExternalField.Notes);
            if (row.Body is { } body)
            {
                if (notes is null)
                {
                    notes = body;
                    supplied.Add("Notes");
                }
                else
                {
                    legacyBody = body;
                    _findings.Add(new ImportFinding(ImportSeverity.Info, Entity, displayKey, ImportFindingCodes.PreservedText, "Notes"));
                }
            }

            if (chosenNumber is not null && CrFor(chosenNumber) is { } cr)
            {
                links.Add(new TransferLink(id, cr.Id, null));

                // B-13.7: a linked task's own title is kept as PreLinkTitle; its CR-level values are proposals for the CR.
                preLinkTitle = title;
                ownTitle = null;
                AddTaskValue(cr, "Title", title);
                AddTaskValue(cr, "TaskNumber", taskNumber);
                AddTaskValue(cr, "Squad", squad);
                AddTaskValue(cr, "Owner", owner);
            }
            else
            {
                supplied.Add("OwnTitle");
                if (title is null)
                {
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.NoTitle, "Title"));
                }
            }

            var legacy = Legacy(table, row, map, legacyBody);
            var unlinkedValues = chosenNumber is null && (taskNumber ?? squad ?? owner) is not null
                ? MergeLegacy(legacy, ("taskNumber", taskNumber), ("squad", squad), ("owner", owner))
                : legacy;
            var preserved = preLinkTitle is not null || unselected.Count > 0 || unlinkedValues is not null
                ? new TransferPreserved(preLinkTitle, unselected, unlinkedValues)
                : null;
            if (preserved is not null)
            {
                supplied.Add("Preserved");
            }

            // ADR 0027: a mapped checklist column is a supplied collection: in a merge it replaces the checklist, and an
            // empty cell clears it (B-12). Otherwise a Markdown record's checkboxes are its checklist (B-13.3).
            var items = row.Checklist ?? [];
            var suppliesChecklist = items.Count > 0;
            if (columns.ContainsKey(ExternalField.ChecklistItems))
            {
                var cell = Cell(row, columns, ExternalField.ChecklistItems);
                if (cell is { HasNonTextItems: true })
                {
                    // A list with objects or nested lists is reported, never read as a shorter (or empty) checklist.
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.InvalidChecklist, nameof(ExternalField.ChecklistItems)));
                    suppliesChecklist = false;
                }
                else if (items.Count > 0)
                {
                    // Two checklists for one record: neither is dropped silently.
                    _findings.Add(new ImportFinding(ImportSeverity.Error, Entity, displayKey, ImportFindingCodes.ChecklistConflict, nameof(ExternalField.ChecklistItems)));
                }
                else
                {
                    items = ExternalValues.ParseChecklist(cell);
                    suppliesChecklist = true;
                }
            }

            List<TransferChecklistItem>? checklist = null;
            if (suppliesChecklist)
            {
                checklist = [.. items.Select((item, index) => new TransferChecklistItem(DerivedId(id, index), item.Text.Trim(), item.IsDone, (index + 1) * 1024L))];
                supplied.Add("ChecklistItems");
            }

            var created = Created(row, columns, Entity, displayKey);
            return new TransferTask
            {
                Id = id,
                OwnTitle = ownTitle,
                Service = Text(row, columns, ExternalField.Service),
                StatusId = Value(LookupKind.TaskStatus, Text(row, columns, ExternalField.Status), Entity, displayKey),
                PriorityId = Value(LookupKind.Priority, Text(row, columns, ExternalField.Priority), Entity, displayKey),
                DesignerId = FirstPerson(Cell(row, columns, ExternalField.Designers), "DesignerId", Entity, displayKey),
                DeveloperId = FirstPerson(Cell(row, columns, ExternalField.Developers), "DeveloperId", Entity, displayKey),
                TagIds = Values(LookupKind.Tag, Cell(row, columns, ExternalField.Tags), Entity, displayKey),
                Notes = notes,
                StartDate = Date(row, columns, ExternalField.StartDate, Entity, displayKey, supplied),
                EndDate = Date(row, columns, ExternalField.EndDate, Entity, displayKey, supplied),
                IsArchived = Boolean(row, columns, ExternalField.Archived, Entity, displayKey),
                ChecklistItems = checklist,
                CustomValues = CustomValues(row, map, Entity, displayKey, supplied),
                Preserved = preserved,
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = supplied,
            };
        }

        private static void AddTaskValue(CrDraft cr, string field, string? value)
        {
            if (value is not null)
            {
                cr.TaskValues.Add((field, value));
            }
        }

        private static string? MergeLegacy(string? legacy, params (string Name, string? Value)[] values)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (legacy is not null)
                {
                    using var existing = JsonDocument.Parse(legacy);
                    foreach (var property in existing.RootElement.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }

                foreach (var (name, value) in values)
                {
                    if (value is not null)
                    {
                        writer.WriteString(name, value);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>
        /// A stable checklist item ID derived from its task and position, so importing the same source again matches
        /// the same items instead of replacing them with new identities.
        /// </summary>
        private static Guid DerivedId(Guid parent, int index)
        {
            Span<byte> input = stackalloc byte[20];
            parent.TryWriteBytes(input);
            BitConverter.TryWriteBytes(input[16..], index);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(input, hash);
            return new Guid(hash[..16]);
        }
    }
}
