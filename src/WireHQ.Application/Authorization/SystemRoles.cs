namespace WireHQ.Application.Authorization;

/// <summary>
/// The default roles seeded into every new organization, with their permission sets. Orgs on
/// the Enterprise edition may additionally define custom roles. <see cref="Owner"/> always
/// holds every permission. (docs/04-security.md)
/// </summary>
public static class SystemRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Member = "Member";
    public const string Billing = "Billing";
    public const string Auditor = "Auditor";

    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Definitions { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            [Owner] = Permissions.All.Select(p => p.Key).ToArray(),

            [Admin] =
            [
                Permissions.Organization.Read, Permissions.Organization.Update,
                Permissions.Users.Read, Permissions.Users.Invite, Permissions.Users.Update, Permissions.Users.Remove,
                Permissions.Teams.Read, Permissions.Teams.Manage,
                Permissions.Roles.Read, Permissions.Roles.Manage,
                Permissions.Audit.Read,
                Permissions.Sso.Manage,
                Permissions.Ldap.Read, Permissions.Ldap.Manage,
                Permissions.AccessPolicy.Read, Permissions.AccessPolicy.Manage,
                Permissions.ApiKeys.Manage,
                Permissions.Modules.Manage,
                Permissions.Branding.Manage,
                Permissions.Notifications.Manage,
                Permissions.Notifications.Acknowledge,
            ],

            [Member] =
            [
                Permissions.Organization.Read,
                Permissions.Users.Read,
                Permissions.Teams.Read,
            ],

            [Billing] =
            [
                Permissions.Organization.Read, Permissions.Organization.Update,
            ],

            [Auditor] =
            [
                Permissions.Organization.Read,
                Permissions.Users.Read,
                Permissions.Teams.Read,
                Permissions.Roles.Read,
                Permissions.Audit.Read,
            ],
        };
}
