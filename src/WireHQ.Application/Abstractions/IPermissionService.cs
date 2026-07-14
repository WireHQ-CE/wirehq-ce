namespace WireHQ.Application.Abstractions;

/// <summary>
/// Resolves the effective permission set for a membership (its roles' permissions, unioned).
/// Used to populate the access token at login and the current-user context per request.
/// Results are cacheable per session and invalidated on role/membership change.
/// </summary>
public interface IPermissionService
{
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid membershipId, CancellationToken cancellationToken);
}
