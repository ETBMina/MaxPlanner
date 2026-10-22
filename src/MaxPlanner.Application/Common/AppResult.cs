namespace MaxPlanner.Application.Common;

/// <summary>Either a value or a typed <see cref="AppError"/>. Use cases return failures instead of throwing.</summary>
public readonly struct AppResult<T>
{
    private readonly T? _value;

    internal AppResult(T value)
    {
        _value = value;
        Error = null;
    }

    internal AppResult(AppError error)
    {
        _value = default;
        Error = error;
    }

    public AppError? Error { get; }

    public bool IsSuccess => Error is null;

    /// <exception cref="InvalidOperationException">The result is a failure.</exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed with {Error!.Code}.");
}

public static class AppResult
{
    public static AppResult<T> Success<T>(T value) => new(value);

    public static AppResult<T> Failure<T>(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }
}
