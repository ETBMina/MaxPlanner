namespace MaxPlanner.Domain.Common;

/// <summary>Structural checks shared by the <c>Rehydrate</c> factories that rebuild records from storage.</summary>
internal static class StoredRecord
{
    /// <summary>Revisions start at 1 (T-05) and a record cannot be updated before it was created.</summary>
    public static DomainError? CheckAudit(DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc, long revision)
    {
        if (revision < 1)
        {
            return new DomainError(DomainErrorCodes.InvalidStoredValue, "Revision");
        }

        return updatedAtUtc < createdAtUtc ? new DomainError(DomainErrorCodes.InvalidStoredValue, "UpdatedAtUtc") : null;
    }
}
