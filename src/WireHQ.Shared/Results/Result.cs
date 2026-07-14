namespace WireHQ.Shared.Results;

/// <summary>
/// The functional result type used by every use case. Expected failures are values, not
/// exceptions — handlers return <see cref="Failure(Error)"/> instead of throwing, which keeps
/// the happy path clean and makes the failure contract explicit and testable.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        // Invariant: success must carry no error; failure must carry one.
        if (isSuccess && error != Error.None)
        {
            throw new InvalidOperationException("A successful result cannot contain an error.");
        }

        if (!isSuccess && error == Error.None)
        {
            throw new InvalidOperationException("A failing result must contain an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    /// <summary>Lift an error into a failed result so methods returning <see cref="Result"/> can
    /// simply <c>return someError;</c> (mirrors the implicit operator on <see cref="Result{TValue}"/>).</summary>
    public static implicit operator Result(Error error) => Failure(error);

    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.Success(value);

    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.Failure(error);
}
