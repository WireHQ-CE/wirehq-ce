namespace WireHQ.Shared.Results;

/// <summary>
/// A validation failure carrying per-field messages. The HTTP boundary maps this to a 400
/// ProblemDetails with the <c>errors</c> map (see docs/06-api-design.md). It is still an
/// <see cref="Error"/>, so it flows through <see cref="Result"/> like any other failure.
/// </summary>
public sealed record ValidationError : Error
{
    public ValidationError(IReadOnlyDictionary<string, string[]> errors)
        : base("validation_error", "One or more validation errors occurred.", ErrorType.Validation) =>
        Errors = errors;

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
