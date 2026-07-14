using WireHQ.Domain.Common;

namespace WireHQ.Domain.Identity;

public sealed record UserRegistered(Guid UserId, string Email, string Name) : IDomainEvent;

public sealed record UserEmailVerified(Guid UserId) : IDomainEvent;

public sealed record UserPasswordChanged(Guid UserId) : IDomainEvent;

public sealed record UserLockedOut(Guid UserId, DateTimeOffset UntilUtc) : IDomainEvent;

public sealed record UserMfaEnabled(Guid UserId) : IDomainEvent;

public sealed record UserMfaDisabled(Guid UserId) : IDomainEvent;
