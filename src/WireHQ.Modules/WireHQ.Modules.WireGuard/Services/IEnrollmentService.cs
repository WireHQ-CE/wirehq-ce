using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// The Enrollment layer (docs/11-wireguard-module.md §7/§9). Pure, DB-free CSV parsing + per-row
/// validation so it is fully unit-testable; duplicate detection, address allocation, and persistence
/// live in the handlers (which have the DB + provider). Header matching is case-insensitive and
/// order-independent; <c>Name</c> and <c>Email</c> are required, the rest optional.
/// </summary>
public interface IEnrollmentService
{
    /// <summary>Maximum number of data rows accepted in one upload (DoS guard).</summary>
    int MaxRows { get; }

    /// <summary>Maximum accepted upload size in bytes (DoS guard; enforced at the endpoint).</summary>
    int MaxBytes { get; }

    /// <summary>Parses CSV text into rows. Fails on an empty file, missing required columns, or row-cap overflow.</summary>
    Result<IReadOnlyList<ParsedEnrollmentRow>> Parse(string csvText);

    /// <summary>Validates a single row's fields against the target network. Returns <c>null</c> when valid, else the reason.</summary>
    string? Validate(EnrollmentRow row, string networkCidr);

    /// <summary>Normalizes a bare address to host/prefix form (e.g. <c>10.8.0.5</c> → <c>10.8.0.5/32</c>).</summary>
    string NormalizeAddress(string address);
}

/// <summary>A logical enrollment row (a device to create), independent of CSV vs JSON transport.</summary>
public sealed record EnrollmentRow(
    string? Name,
    string? Email,
    string? Department,
    string? DeviceType,
    string? AssignedAddress,
    IReadOnlyList<string> AllowedIps);

/// <summary>A parsed CSV row with its 1-based line number (for error reporting in the preview).</summary>
public sealed record ParsedEnrollmentRow(int RowNumber, EnrollmentRow Row);
