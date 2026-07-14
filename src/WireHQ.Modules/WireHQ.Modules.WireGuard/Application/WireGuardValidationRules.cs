using System.Net;
using FluentValidation;

namespace WireHQ.Modules.WireGuard.Application;

/// <summary>Reusable FluentValidation rules shared across WireGuard commands.</summary>
internal static class WireGuardValidationRules
{
    /// <summary>
    /// Every DNS entry must parse as an IP address. Applied to the whole collection (not per element) so
    /// the validation error keys on <c>dns</c> — matching the form's DNS field — rather than <c>dns[1]</c>.
    /// A null/empty list is allowed (DNS is optional); entries are trimmed before parsing.
    /// </summary>
    public static IRuleBuilderOptions<T, IReadOnlyList<string>?> ValidDnsServers<T>(
        this IRuleBuilder<T, IReadOnlyList<string>?> rule) =>
        // Blank entries are tolerated (SetDns drops them); every non-blank entry must parse as an IP.
        rule.Must(dns => dns is null || dns.All(d => string.IsNullOrWhiteSpace(d) || IPAddress.TryParse(d.Trim(), out _)))
            .WithMessage("Each DNS server must be a valid IP address (e.g. 1.1.1.1).");
}
