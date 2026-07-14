using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WireHQ.Application.Abstractions;

namespace WireHQ.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Drives Postgres Row-Level Security (Layer 2 tenant isolation) by setting the per-connection GUCs from
/// the request-scoped <see cref="ITenantContext"/> on <b>every</b> connection open — so a pooled connection
/// can never carry a previous request's tenant. <c>app.current_org</c> scopes the RLS policy to the active
/// org; <c>app.bypass_rls</c> opts the trusted cross-tenant paths (platform handlers, session minting, org
/// provisioning, the dispatcher/reconciler claim, boot seeders) out. With no org and no bypass the policy
/// matches no rows (fail-closed). Set at session level (outside any transaction), which is valid here:
/// <c>ConnectionOpened</c> fires before EF begins its transaction. (docs/03-multi-tenancy.md, ADR-027)
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string SetGucsSql =
        "SELECT set_config('app.current_org', @org, false), set_config('app.bypass_rls', @bypass, false);";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        // SetGucsSql is a compile-time constant; the org id and bypass flag are passed as bound parameters
        // (@org/@bypass) via set_config — never string-interpolated. No injection surface here.
        // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
        command.CommandText = SetGucsSql;

        var org = command.CreateParameter();
        org.ParameterName = "org";
        org.Value = tenant.OrganizationId?.ToString() ?? string.Empty;
        command.Parameters.Add(org);

        var bypass = command.CreateParameter();
        bypass.ParameterName = "bypass";
        bypass.Value = tenant.IsPlatformScope || tenant.BypassTenantIsolation ? "on" : "off";
        command.Parameters.Add(bypass);

        return command;
    }
}
