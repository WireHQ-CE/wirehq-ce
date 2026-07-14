namespace WireHQ.Api.Audit;

/// <summary>
/// Maps a WireHQ audit <c>action</c> key to its OCSF (Open Cybersecurity Schema Framework, https://schema.ocsf.io)
/// class + activity and the equivalent CEF signature, so the Enterprise audit→SIEM export (docs/15 §11/§16,
/// Phase 7) emits events a SIEM recognises rather than opaque vendor strings.
/// <para>
/// The mapping is deliberately <b>prefix-driven</b> — the first dot-segment (<c>auth</c>, <c>wg</c>, <c>orch</c>,
/// <c>platform</c>, …) plus the verb suffix — which makes it <b>total</b>: every current action key, and every
/// future one added under an existing prefix, is classified by construction, and an unknown prefix falls back to
/// a generic API Activity event (never silently dropped). A brand-new top-level prefix is the only thing that
/// needs a deliberate mapping decision — that is the §19.4 DoD hook ("is this action in the SIEM mapping?").
/// </para>
/// </summary>
public static class SiemActionMap
{
    // OCSF category_uid
    private const int IamCategory = 3;          // Identity & Access Management
    private const int AppActivityCategory = 6;  // Application Activity
    private const string IamCategoryName = "Identity & Access Management";
    private const string AppActivityCategoryName = "Application Activity";

    // OCSF class_uid
    private const int AuthenticationClass = 3002;
    private const int AccountChangeClass = 3001;
    private const int ApiActivityClass = 6003;

    public static SiemEvent Map(string? action)
    {
        var parts = (action ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var prefix = parts.Length > 0 ? parts[0] : string.Empty;
        var segment2 = parts.Length > 1 ? parts[1] : string.Empty;
        var verb = parts.Length > 0 ? parts[^1] : string.Empty;

        // --- Authentication / session events → OCSF IAM / Authentication [3002] ---
        var isAuthentication =
            prefix is "auth" ||
            (prefix is "platform" && segment2 is "impersonation") ||
            (prefix is "account" && verb is "session_revoked" or "sessions_revoked_all");

        if (isAuthentication)
        {
            var (id, name) = verb switch
            {
                "login" or "started" or "mfa_verify" => (1, "Logon"),
                "logout" or "ended" or "session_revoked" or "sessions_revoked_all" => (2, "Logoff"),
                _ => (99, "Other"),
            };
            return new SiemEvent(IamCategory, IamCategoryName, AuthenticationClass, "Authentication", id, name);
        }

        // --- Account / identity changes → OCSF IAM / Account Change [3001] ---
        var isAccountChange =
            prefix is "account" or "mfa" ||
            (prefix is "identity" && segment2 is "users") ||
            (prefix is "platform" && segment2 is "customer" && verb is "user_added" or "user_removed");

        if (isAccountChange)
        {
            var (id, name) =
                Has(verb, "password") ? (3, "Password Change") :
                prefix is "mfa" && Has(verb, "enabled") ? (7, "MFA Factor Enable") :
                prefix is "mfa" && Has(verb, "disabled") ? (8, "MFA Factor Disable") :
                Has(verb, "invite", "added", "created") ? (1, "Create") :
                Has(verb, "enabled") ? (2, "Enable") :
                Has(verb, "disabled") ? (4, "Disable") :
                Has(verb, "removed", "deleted") ? (5, "Delete") :
                (99, "Other");
            return new SiemEvent(IamCategory, IamCategoryName, AccountChangeClass, "Account Change", id, name);
        }

        // --- Everything else: resource activity → OCSF Application Activity / API Activity [6003] ---
        // Match base verbs ("create"/"update"/"delete") so both the bare ("identity.teams.create") and the
        // past-tense ("wg.network.created") suffixes classify via Contains.
        var (apiId, apiName) =
            Has(verb, "create", "queued", "minted", "enrolled", "executed", "started", "submitted", "accepted", "invite", "added") ? (1, "Create") :
            Has(verb, "delete", "removed") ? (4, "Delete") :
            Has(verb, "revealed", "exported", "tested", "read") ? (2, "Read") :
            Has(verb, "update", "changed", "rotated", "bound", "adopted", "reactivated", "processed", "enabled", "disabled", "revoked", "reconverge", "completed") ? (3, "Update") :
            string.IsNullOrEmpty(verb) ? (0, "Unknown") : (99, "Other");
        return new SiemEvent(AppActivityCategory, AppActivityCategoryName, ApiActivityClass, "API Activity", apiId, apiName);
    }

    // Keyword match on the verb token so multi-word suffixes still classify (e.g. "config_exported" → Read,
    // "cert_rotated" → Update) without enumerating every literal.
    private static bool Has(string verb, params string[] tokens) =>
        tokens.Any(t => verb.Contains(t, StringComparison.Ordinal));
}

/// <summary>The OCSF classification of one audit action — category + class + activity (docs/15 §11/§16).</summary>
public sealed record SiemEvent(
    int CategoryUid,
    string CategoryName,
    int ClassUid,
    string ClassName,
    int ActivityId,
    string ActivityName)
{
    /// <summary>OCSF <c>type_uid</c> = class_uid × 100 + activity_id.</summary>
    public int TypeUid => (ClassUid * 100) + ActivityId;
}
