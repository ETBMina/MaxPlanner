namespace MaxPlanner.Domain.Common;

/// <summary>A rule violation. <see cref="Field"/> names the offending input when there is one.</summary>
public sealed record DomainError(string Code, string? Field = null, long? Limit = null);

/// <summary>Either a value or the first rule it violated. Domain rules never throw for invalid input.</summary>
public readonly struct DomainResult<T>
{
    private readonly T? _value;

    internal DomainResult(T value)
    {
        _value = value;
        Error = null;
    }

    internal DomainResult(DomainError error)
    {
        _value = default;
        Error = error;
    }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed with {Error!.Code}.");

    public DomainResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsSuccess ? new DomainResult<TOut>(map(_value!)) : new DomainResult<TOut>(Error!);

    public DomainResult<TOut> Bind<TOut>(Func<T, DomainResult<TOut>> next) =>
        IsSuccess ? next(_value!) : new DomainResult<TOut>(Error!);
}

public static class DomainResult
{
    public static DomainResult<T> Success<T>(T value) => new(value);

    public static DomainResult<T> Failure<T>(DomainError error) => new(error);

    public static DomainResult<T> Failure<T>(string code, string? field = null, long? limit = null) =>
        new(new DomainError(code, field, limit));
}
