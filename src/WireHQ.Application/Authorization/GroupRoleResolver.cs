namespace WireHQ.Application.Authorization;

/// <summary>An ordered "IdP/directory group → WireHQ role" rule.</summary>
public readonly record struct GroupRoleRule(string Group, Guid RoleId);

/// <summary>
/// The shared, pure resolution of a set of the user's groups against an ordered list of group→role rules — the
/// one algorithm behind SSO's, SCIM's, and LDAP directory sync's role mapping (docs/23-ldap-directory-sync.md,
/// D-5). Deliberately <b>kept core</b> (idle in the CE until the directory-sync module is licensed): the storage
/// differs per source — SSO's mapping table is stripped from the CE while directory sync's is CE-module-kept, so
/// they cannot share a table — but the resolution is identical and lives here once. First rule whose group the
/// user is in wins (case-insensitive); no match falls back to the default role.
/// </summary>
public static class GroupRoleResolver
{
    public static Guid? Resolve(IReadOnlyList<string> userGroups, IReadOnlyList<GroupRoleRule> orderedRules, Guid? defaultRoleId)
    {
        if (userGroups.Count > 0 && orderedRules.Count > 0)
        {
            foreach (var rule in orderedRules)
            {
                if (userGroups.Contains(rule.Group, StringComparer.OrdinalIgnoreCase))
                {
                    return rule.RoleId;
                }
            }
        }

        return defaultRoleId;
    }
}
