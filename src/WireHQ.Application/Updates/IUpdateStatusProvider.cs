namespace WireHQ.Application.Updates;

/// <summary>
/// Supplies the install's current update situation to <c>GET /api/v1/updates/status</c>. Kept-core so the main CI
/// exercises the endpoint over the no-op; the SaaS build binds <see cref="NoUpdateStatus"/> (SaaS is
/// WireHQ-operated + auto-deployed — it never polls and always reports up-to-date), while the Community Edition
/// binds a poller-backed implementation. Registered as a <b>singleton</b> (a background poller writes it; a
/// scoped lifetime would lose the polled snapshot between requests — docs/30 U-6). (docs/30 U-7)
/// </summary>
public interface IUpdateStatusProvider
{
    /// <summary>The latest known status. <paramref name="currentVersion"/> is the install's build-stamped version,
    /// resolved by the caller (the API) so the provider stays free of an assembly dependency.</summary>
    UpdateStatus Current(string currentVersion);
}

/// <summary>
/// The default provider: always "up to date". Bound in every edition except a CE that has wired the real poller —
/// so SaaS never phones home and shows no banner. (docs/30 U-6/U-10)
/// </summary>
public sealed class NoUpdateStatus : IUpdateStatusProvider
{
    public UpdateStatus Current(string currentVersion) => new(
        UpdateState.UpToDate, currentVersion, currentVersion, false, UpdateSeverity.None, false, false, null, null, null);
}
