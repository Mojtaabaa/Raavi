namespace Raavi.Application.Common;

/// <summary>Stable error descriptor. <see cref="Code"/> is a dotted, localizable key (e.g. "tenant.not_found").</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Validation);

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    Unavailable,

    /// <summary>412: the If-Match precondition did not match the current revision.</summary>
    PreconditionFailed,

    /// <summary>428: the request must be conditional (If-Match).</summary>
    PreconditionRequired,

    /// <summary>429: a per-user or per-tenant limit was reached; try again later.</summary>
    TooManyRequests,
}

/// <summary>Outcome of an application use case. Expected failures are values; exceptions are reserved for bugs and infrastructure faults.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static Result<T> Success(T value) => new(value, null);

    public static new Result<T> Failure(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}
