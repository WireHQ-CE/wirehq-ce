using WireHQ.Shared.Results;

namespace WireHQ.Application.Abstractions.Orchestration;

/// <summary>
/// A generic, opaque extension point on the agent gateway (docs/23-ldap-directory-sync.md §6/§7, ADR-040) — the
/// seam that lets a feature hand <b>non-WireGuard</b> work to an enrolled agent without the <b>core</b> gateway
/// (agents are a CE feature) ever knowing what the work is. The gateway aggregates pending tasks across all
/// registered providers and routes a posted result back to whichever one owns the task; the payload + result are
/// opaque JSON. LDAP directory sync is the first provider (`kind = "directory.sync"`, wave 3): the agent binds to
/// the directory <b>locally</b> (agent-local credential custody, D-3), so the task carries only the query spec,
/// and the agent posts the pulled snapshot back — the provider then reconciles. In the CE no provider is
/// registered, so the gateway's task endpoints simply return nothing (a harmless idle seam).
/// </summary>
public interface IAgentTaskProvider
{
    /// <summary>The pending tasks this provider has queued for the given agent.</summary>
    Task<IReadOnlyList<AgentTaskDescriptor>> GetPendingAsync(Guid organizationId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Ingest an agent's result for one of this provider's tasks. Returns <c>NotFound</c> (so the gateway can try
    /// the next provider) when the task id is not one this provider owns; any other outcome is authoritative.
    /// </summary>
    Task<Result> SubmitResultAsync(Guid organizationId, Guid agentId, Guid taskId, string resultJson, CancellationToken cancellationToken);
}

/// <summary>An opaque unit of agent work: a stable id, a <paramref name="Kind"/> discriminator the agent switches
/// on, and a JSON <paramref name="PayloadJson"/> the agent interprets (never carries secrets — D-3).</summary>
public sealed record AgentTaskDescriptor(Guid TaskId, string Kind, string PayloadJson);
