namespace WireHQ.Shared.Results;

/// <summary>
/// A <see cref="Result"/> that carries a value on success. Accessing <see cref="Value"/> on a
/// failed result throws — failures must be handled before the value is read.
/// </summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(TValue value)
        : base(true, Error.None) => _value = value;

    private Result(Error error)
        : base(false, error) => _value = default;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public static Result<TValue> Success(TValue value) => new(value);

    public new static Result<TValue> Failure(Error error) => new(error);

    /// <summary>Implicitly lift a value into a successful result for ergonomic returns.</summary>
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    /// <summary>Implicitly lift an error into a failed result for ergonomic returns.</summary>
    public static implicit operator Result<TValue>(Error error) => Failure(error);

    /// <summary>Fold both branches into a single value — handy at the HTTP boundary.</summary>
    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<Error, TResult> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(Error);
}
