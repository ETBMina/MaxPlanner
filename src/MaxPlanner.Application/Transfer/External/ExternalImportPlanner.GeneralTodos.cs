using System.Globalization;

namespace MaxPlanner.Application.Transfer.External;

public static partial class ExternalImportPlanner
{
    private sealed partial class Builder
    {
        // ----- General to-dos -----
        private TransferGeneralTodo? ReadTodo(ExternalTable table, ExternalRow row, TableColumns map)
        {
            const string Entity = ImportEntity.GeneralTodo;
            var columns = map.Fields;
            var displayKey = table.Name + "#" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            if (RecordId(Entity, SourceKey(row, columns), displayKey) is not { } id)
            {
                return null;
            }

            var created = Created(row, columns, Entity, displayKey);
            return new TransferGeneralTodo
            {
                Id = id,
                Text = Text(row, columns, ExternalField.Title),
                IsCompleted = Boolean(row, columns, ExternalField.Done, Entity, displayKey),
                CreatedAtUtc = created,
                UpdatedAtUtc = UpdatedFor(created),
                SuppliedFields = columns.ContainsKey(ExternalField.Done) ? Members("Text", "IsCompleted") : Members("Text"),
            };
        }
    }
}
