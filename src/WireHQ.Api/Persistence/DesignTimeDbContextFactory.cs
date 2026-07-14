using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Infrastructure.Persistence;
using WireHQ.Modules.Orchestration.Persistence;
using WireHQ.Modules.WireGuard.Persistence;

namespace WireHQ.Api.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Lives in the Api (the composition root) so
/// it can include the feature modules' model contributors — without them the generated migration
/// would miss module schemas (e.g. <c>wg</c>). Add a module's contributor here when it ships.
///
/// Generate the baseline (run from the repo root, with the .NET SDK available — e.g. in the
/// sdk:9.0 container):
///   dotnet ef migrations add InitialCreate -p src/WireHQ.Infrastructure -s src/WireHQ.Api -o Persistence/Migrations
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        // Prefer the owner/Admin connection — migrations are DDL the runtime wirehq_app role can't perform.
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Admin")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=wirehq;Username=wirehq;Password=wirehq";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "core");
            })
            .UseSnakeCaseNamingConvention()
            .Options;

        IModelConfigurationContributor[] contributors = [new WireGuardModelContributor(), new OrchestrationModelContributor()];
        return new ApplicationDbContext(options, new NullTenantContext(), contributors);
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? OrganizationId => null;
        public string? OrganizationSlug => null;
        public bool IsPlatformScope => false;
        public bool BypassTenantIsolation => false;
    }
}
