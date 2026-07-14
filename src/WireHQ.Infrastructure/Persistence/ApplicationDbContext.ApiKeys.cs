using Microsoft.EntityFrameworkCore;
using WireHQ.Domain.ApiKeys;

namespace WireHQ.Infrastructure.Persistence;

/// <summary>The API-keys slice of the concrete context (docs/26-api-keys-webhooks.md §4). Kept-core — ships in
/// every edition (entitlement-gated, not stripped).</summary>
public sealed partial class ApplicationDbContext
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
}
