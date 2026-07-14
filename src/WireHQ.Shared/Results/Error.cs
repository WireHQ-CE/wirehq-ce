namespace WireHQ.Shared.Results;

/// <summary>
/// Classifies an <see cref="Error"/> so a single, central mapper can translate it to the
/// right transport (e.g. HTTP status). Use cases return typed errors; they never decide
/// HTTP concerns themselves.
/// </summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
}

/// <summary>
/// A stable, machine-readable error. <see cref="Code"/> is the contract clients branch on
/// (e.g. "identity.email_taken"); <see cref="Description"/> is human-facing and may change.
/// Not sealed so richer errors (e.g. <see cref="ValidationError"/>) can extend it while still
/// flowing through <see cref="Result"/>.
/// </summary>
public record Error(string Code, string Description, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);
    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);
    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);
}
