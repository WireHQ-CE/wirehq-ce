namespace WireHQ.Application.Abstractions;

/// <summary>
/// Tracks which tenants are in time-boxed "diagnostic mode" — their requests log at Debug verbosity without a
/// redeploy (docs/15 §4). In-memory + transient by design (a process restart clears the windows); the
/// enable/disable action itself is audited. Read on the logging hot path, so implementations must be fast and
/// lock-free, and windows expire on their own.
/// </summary>
public interface IDiagnosticModeStore
{
    /// <summary>Raise this tenant to Debug verbosity until <paramref name="until"/>.</summary>
    void Enable(Guid organizationId, DateTimeOffset until);

    /// <summary>Return this tenant to the normal verbosity immediately.</summary>
    void Disable(Guid organizationId);

    /// <summary>True if the tenant is currently in diagnostic mode (and the window has not expired).</summary>
    bool IsEnabled(Guid organizationId);

    /// <summary>The currently-active windows (tenant → expiry), for the operator console.</summary>
    IReadOnlyCollection<KeyValuePair<Guid, DateTimeOffset>> Active();
}
