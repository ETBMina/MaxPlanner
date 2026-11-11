using System.Globalization;
using MaxPlanner.Domain.Values;
using MaxPlanner.Domain.Workflow;

namespace MaxPlanner.Application.Transfer.External;

public static partial class ExternalImportPlanner
{
    private sealed partial class Builder
    {
        // ----- Reviews -----
        private TransferReview? ReadReview(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.Review;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            if (RecordId(Entity, SourceKey(row, columns), displayKey) is not { } id)
            {
                return null;
            }

            var supplied = columns.Keys.Where(f => f is not (ExternalField.SourceId or ExternalField.Created)).Select(f => f switch
            {
                ExternalField.CrNumber => "CrNumberText",
                ExternalField.TaskNumber => "TaskNumberText",
                ExternalField.Service => "Services",
                ExternalField.Notes => "Comments",
                _ => MemberOf(f),
            }).ToHashSet(StringComparer.Ordinal);
            var done = Boolean(row, columns, ExternalField.Done, Entity, displayKey);
            var completedOn = Date(row, columns, ExternalField.CompletedOn, Entity, displayKey, supplied);
            var developer = Value(LookupKind.Person, Text(row, columns, ExternalField.Developer), Entity, displayKey);
            var created = Created(row, columns, Entity, displayKey);
            return new TransferReview
            {
                Id = id,
                Title = Text(row, columns, ExternalField.Title),
                CrNumberText = Text(row, columns, ExternalField.CrNumber),
                TaskNumberText = Text(row, columns, ExternalField.TaskNumber),
                Services = List(Cell(row, columns, ExternalField.Service)),
                DeveloperId = developer,
                Comments = Text(row, columns, ExternalField.Notes) ?? row.Body,
                IsDone = done ?? completedOn is not null,
                CompletedOn = completedOn,
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = supplied,
            };
        }
    }
}
