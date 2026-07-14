namespace WireHQ.Modules.Orchestration.Gateway;

/// <summary>
/// Claim types for an authenticated agent principal. Deliberately distinct from the JWT <c>org</c>/<c>plat</c>
/// claims so <c>TenantResolutionMiddleware</c> does not also act on an agent request — the agent auth handler
/// is the sole authority on an agent request's tenant (it calls <c>SetTenant</c> directly).
/// </summary>
public static class AgentGatewayClaims
{
    public const string AgentId = "wirehq:agent_id";
    public const string OrganizationId = "wirehq:agent_org";
}
