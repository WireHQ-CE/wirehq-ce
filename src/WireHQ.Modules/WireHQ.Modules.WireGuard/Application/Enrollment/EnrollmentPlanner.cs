using WireHQ.Modules.WireGuard.Services;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Application.Enrollment;

/// <summary>What will happen to a row: create the peer, skip it (duplicate), or reject it (invalid).</summary>
public enum EnrollmentOutcome
{
    Create,
    Skip,
    Error,
}

/// <summary>A classified row with its resolved address (for Create) and reason (for Skip/Error).</summary>
public sealed record PlannedRow(int RowNumber, EnrollmentRow Row, EnrollmentOutcome Outcome, string? Reason, string? AssignedAddress);

public sealed record EnrollmentPlan(IReadOnlyList<PlannedRow> Rows)
{
    public int CreateCount => Rows.Count(r => r.Outcome == EnrollmentOutcome.Create);

    public int SkipCount => Rows.Count(r => r.Outcome == EnrollmentOutcome.Skip);

    public int ErrorCount => Rows.Count(r => r.Outcome == EnrollmentOutcome.Error);
}

/// <summary>
/// Turns parsed rows into a plan (Create/Skip/Error + the address each Create would get), applying the
/// same validation, duplicate detection (in-file + against existing peers, by email and by address),
/// and batch-safe address allocation that both the preview and the execute use — so the dry-run and
/// the real import can never disagree on what would happen. v1 conflict policy is skip-duplicates
/// (overwrite is a deliberate follow-up, see docs/11 §7). (docs/11-wireguard-module.md §7)
/// </summary>
public static class EnrollmentPlanner
{
    public static async Task<EnrollmentPlan> PlanAsync(
        IReadOnlyList<ParsedEnrollmentRow> parsed,
        string networkCidr,
        IEnumerable<string> existingEmails,
        IEnumerable<string> existingAddressHosts,
        IEnrollmentService enrollment,
        Func<int, IReadOnlyCollection<string>, CancellationToken, Task<Result<IReadOnlyList<string>>>> allocate,
        CancellationToken cancellationToken)
    {
        var seenEmails = new HashSet<string>(existingEmails, StringComparer.OrdinalIgnoreCase);
        var seenAddresses = new HashSet<string>(existingAddressHosts, StringComparer.OrdinalIgnoreCase);

        var staged = new List<Stage>(parsed.Count);
        var reservedExplicit = new List<string>();

        foreach (var p in parsed)
        {
            var error = enrollment.Validate(p.Row, networkCidr);
            if (error is not null)
            {
                staged.Add(new Stage(p, EnrollmentOutcome.Error, error, null, Auto: false));
                continue;
            }

            // Email is the primary dedup key; first occurrence wins.
            if (!seenEmails.Add(p.Row.Email!.ToLowerInvariant()))
            {
                staged.Add(new Stage(p, EnrollmentOutcome.Skip, "Duplicate email — a peer with this email already exists or appears earlier in the file.", null, Auto: false));
                continue;
            }

            if (p.Row.AssignedAddress is { } requested)
            {
                var host = HostOf(requested);
                if (!seenAddresses.Add(host))
                {
                    staged.Add(new Stage(p, EnrollmentOutcome.Skip, $"Address {requested} is already in use.", null, Auto: false));
                    continue;
                }

                var normalized = enrollment.NormalizeAddress(requested);
                reservedExplicit.Add(normalized);
                staged.Add(new Stage(p, EnrollmentOutcome.Create, null, normalized, Auto: false));
            }
            else
            {
                staged.Add(new Stage(p, EnrollmentOutcome.Create, null, null, Auto: true));
            }
        }

        var autoIndexes = staged
            .Select((s, i) => (s, i))
            .Where(x => x is { s.Outcome: EnrollmentOutcome.Create, s.Auto: true })
            .Select(x => x.i)
            .ToList();

        if (autoIndexes.Count > 0)
        {
            var allocation = await allocate(autoIndexes.Count, reservedExplicit, cancellationToken);
            if (allocation.IsFailure)
            {
                // Not enough free addresses — reject the auto rows rather than failing the whole batch.
                foreach (var i in autoIndexes)
                {
                    staged[i] = staged[i] with { Outcome = EnrollmentOutcome.Error, Reason = "No free address available in the network." };
                }
            }
            else
            {
                for (var n = 0; n < autoIndexes.Count; n++)
                {
                    staged[autoIndexes[n]] = staged[autoIndexes[n]] with { Address = allocation.Value[n] };
                }
            }
        }

        var rows = staged
            .Select(s => new PlannedRow(s.Parsed.RowNumber, s.Parsed.Row, s.Outcome, s.Reason, s.Address))
            .ToList();

        return new EnrollmentPlan(rows);
    }

    public static string HostOf(string address) => address.Split('/')[0];

    private sealed record Stage(ParsedEnrollmentRow Parsed, EnrollmentOutcome Outcome, string? Reason, string? Address, bool Auto);
}
