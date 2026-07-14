using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.ApiKeys;

namespace WireHQ.Application.Abstractions.Persistence;

/// <summary>
/// The API-keys slice of the persistence port (docs/26-api-keys-webhooks.md §4). Kept-core — API keys are an
/// entitlement-gated platform capability, not a SaaS-only module, so this ships in <b>every</b> edition (the CE
/// defaults orgs to Enterprise). Tenant-owned in the reused <c>identity</c> schema (RLS for free).
/// </summary>
public partial interface IApplicationDbContext
{
    DbSet<ApiKey> ApiKeys { get; }
}
