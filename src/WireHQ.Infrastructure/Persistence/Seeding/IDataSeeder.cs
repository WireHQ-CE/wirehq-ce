namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// A boot-time reference/bootstrap seeder, run by <c>ApplicationDbContextInitializer</c> in ascending
/// <see cref="Order"/> after migrations + RLS are applied (with the tenant bypass set — see Program.cs).
/// The initializer depends only on this seam, never on concrete seeders, so an edition adds or removes
/// seeders by DI registration alone — the Community Edition strip drops a SaaS seeder's file plus its one
/// registration line without touching the initializer (docs/17-community-edition.md §5, the subtractive
/// boundary). Every seeder must be idempotent and must gate itself (e.g. the demo-data seeder runs only
/// when <c>Seed:DemoData=true</c>; the bootstrap seeders only when their <c>Seed:*</c> config is present).
/// </summary>
public interface IDataSeeder
{
    /// <summary>Ascending run order; gaps of 10 by convention so editions can interleave.</summary>
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken = default);
}
