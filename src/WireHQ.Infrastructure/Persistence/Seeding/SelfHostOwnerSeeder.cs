using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Organizations;
using WireHQ.Domain.Identity;
using WireHQ.Domain.ValueObjects;

namespace WireHQ.Infrastructure.Persistence.Seeding;

/// <summary>
/// Bootstraps the first organization <b>Owner</b> for a self-hosted install. A self-hosted instance
/// (the Community Edition) has no platform tier and ships invite-only (<c>Auth:OpenRegistration=false</c>),
/// so without this the operator could never sign in (docs/17-community-edition.md). Opt-in and idempotent,
/// mirroring the platform-admin bootstrap pattern: runs only when <c>Seed:OwnerEmail</c> + <c>Seed:OwnerPassword</c>
/// are configured, and only creates the account if that email doesn't already exist — safe to leave the
/// env vars set across restarts. Change the password in-app after first sign-in. The org is provisioned
/// through the same <see cref="OrganizationProvisioner"/> as self-serve signup, so the Owner gets the
/// full system-role catalog — not a hand-rolled subset.
/// </summary>
public sealed class SelfHostOwnerSeeder(
    ApplicationDbContext dbContext,
    IPasswordHasher passwordHasher,
    OrganizationProvisioner provisioner,
    IConfiguration configuration,
    ILogger<SelfHostOwnerSeeder> logger) : IDataSeeder
{
    public int Order => 60;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var email = configuration["Seed:OwnerEmail"]?.Trim();
        var password = configuration["Seed:OwnerPassword"];

        // Opt-in: only bootstraps when both are supplied (the self-host first-run path).
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var exists = await dbContext.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Email.Value == email, cancellationToken);

        if (exists)
        {
            return; // Idempotent — already bootstrapped (or the address is taken).
        }

        var ownerName = configuration["Seed:OwnerName"]?.Trim();
        var userResult = User.Register(
            email,
            string.IsNullOrWhiteSpace(ownerName) ? "Owner" : ownerName,
            passwordHasher.Hash(password));
        if (userResult.IsFailure)
        {
            logger.LogWarning("Self-host Owner bootstrap skipped: {Error}", userResult.Error.Code);
            return;
        }

        var owner = userResult.Value;
        owner.VerifyEmail();
        dbContext.Users.Add(owner);

        var organizationName = configuration["Seed:OrganizationName"]?.Trim();
        organizationName = string.IsNullOrWhiteSpace(organizationName) ? "WireHQ" : organizationName;
        var slugResult = Slug.FromName(organizationName);
        var slug = slugResult.IsSuccess ? slugResult.Value.Value : "wirehq";

        var provisioned = await provisioner.ProvisionAsync(organizationName, slug, owner.Id, cancellationToken);
        if (provisioned.IsFailure)
        {
            logger.LogWarning("Self-host Owner bootstrap skipped: {Error}", provisioned.Error.Code);
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Bootstrapped the first organization Owner ({Email}) in '{Organization}'.", email, organizationName);
    }
}
