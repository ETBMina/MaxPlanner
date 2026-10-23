using MaxPlanner.Application.Common;
using MaxPlanner.Domain.Common;
using MaxPlanner.Presentation.Shell;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>A command that did not save, with the text to show and the operation it belonged to (T-06.7).</summary>
/// <param name="Message">Localized and actionable; never contains record content beyond the field name.</param>
public sealed record CommandFailure(AppError Error, string Message, OperationId OperationId)
{
    /// <summary>The record changed after the editor started (B-02.9); the draft is kept for review.</summary>
    public bool IsConflict => Error is ConcurrencyConflictError;

    /// <summary>
    /// A storage problem that affects every save, not just this draft (design §2.7: App InfoBar). The
    /// editor still keeps its draft.
    /// </summary>
    public bool IsAppScope => Error is StorageFailureError or CommitOutcomeUnknownError;

    /// <summary>
    /// T-05: after an unknown outcome the same operation ID must be reused for a retry of the same draft, so
    /// the ledger can report an earlier commit instead of applying it twice.
    /// </summary>
    public bool RequiresSameOperationOnRetry => Error is CommitOutcomeUnknownError;

    public static CommandFailure From(AppError error, OperationId operationId, string recordKey, ILocalizedStrings strings) =>
        new(error, CommandErrorText.Describe(error, recordKey, strings), operationId);
}

/// <summary>Maps typed Application errors to localized, actionable text (T-06.7, design §5.0).</summary>
public static class CommandErrorText
{
    /// <param name="recordKey">Resource key of the record noun, for example <c>Record_Task</c>.</param>
    public static string Describe(AppError error, string recordKey, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(strings);
        var record = strings.GetString(recordKey);
        return error switch
        {
            ValidationFailedError validation when validation.Errors.Count > 0 => Describe(validation.Errors[0], strings),
            ConcurrencyConflictError => strings.Format("Error_Conflict", record),
            RecordNotFoundError { WasDeleted: true } => strings.Format("Error_Deleted", record),
            RecordNotFoundError => strings.Format("Error_NotFound", record),
            DuplicateCrNumberError duplicate => strings.Format("CrEditor_DuplicateNumber", duplicate.Number),
            StorageFailureError storage => strings.Format("Error_Storage", StorageFailureText.Describe(storage.Kind, strings)),
            CommitOutcomeUnknownError => strings.GetString("Error_CommitOutcomeUnknown"),
            OperationCancelledError => strings.GetString("Error_Cancelled"),
            _ => strings.GetString("Error_Unexpected"),
        };
    }

    public static string Describe(DomainError error, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(strings);
        var field = strings.GetString("Field_" + (error.Field ?? "Value"));
        return error.Code switch
        {
            DomainErrorCodes.Required => strings.Format("Error_Required", field),
            DomainErrorCodes.TooLong => strings.Format("Error_TooLong", field, error.Limit),
            DomainErrorCodes.TooManyItems => strings.Format("Error_TooManyItems", field, error.Limit),
            DomainErrorCodes.DateRangeInverted => strings.GetString("Error_DateRangeInverted"),
            DomainErrorCodes.ServiceRequiredWhenLinked => strings.GetString("Error_ServiceRequiredWhenLinked"),
            DomainErrorCodes.TitleNotAllowedWhenLinked => strings.GetString("Error_TitleInherited"),
            DomainErrorCodes.ArchivedReadOnly => strings.GetString("Error_ArchivedReadOnly"),
            DomainErrorCodes.UnknownReference => strings.Format("Error_UnknownReference", field),
            DomainErrorCodes.InvalidUrl => strings.GetString("Error_InvalidUrl"),
            DomainErrorCodes.CascadeScopeChanged => strings.GetString("CascadeDelete_ScopeChanged"),
            _ => strings.Format("Error_Invalid", field),
        };
    }
}
