using MaxPlanner.Domain.Common;

namespace MaxPlanner.Application.Common;

/// <summary>Collects every field error of a draft so the editor can mark them all at once (B-02.9).</summary>
internal sealed class ValidationCollector
{
    private readonly List<DomainError> _errors = [];

    public bool HasErrors => _errors.Count > 0;

    /// <summary>Records the result's error, if any, and returns the result so its value can be read once valid.</summary>
    public DomainResult<T> Check<T>(DomainResult<T> result)
    {
        if (result.Error is { } error)
        {
            _errors.Add(error);
        }

        return result;
    }

    public void Add(DomainError? error)
    {
        if (error is not null)
        {
            _errors.Add(error);
        }
    }

    public void AddRange(IEnumerable<DomainError> errors) => _errors.AddRange(errors);

    public ValidationFailedError ToError() => new([.. _errors]);
}
