using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using WireHQ.Modules.WireGuard.Domain;
using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// Default <see cref="IEnrollmentService"/>: parses CSV with CsvHelper (handles quoting/escaping),
/// matching headers case-insensitively and order-independently, and validates each row's fields. It
/// performs no I/O so it can be unit-tested in isolation.
/// </summary>
public sealed partial class EnrollmentService : IEnrollmentService
{
    public int MaxRows => 1_000;

    public int MaxBytes => 1_000_000; // 1 MB

    public Result<IReadOnlyList<ParsedEnrollmentRow>> Parse(string csvText)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            return WireGuardErrors.Enrollment.EmptyFile;
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
            MissingFieldFound = null,   // optional columns may be absent — don't throw
            HeaderValidated = null,     // we validate the required headers ourselves
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        };

        using var reader = new StringReader(csvText);
        using var csv = new CsvReader(reader, config);

        try
        {
            if (!csv.Read() || !csv.ReadHeader())
            {
                return WireGuardErrors.Enrollment.NoRows;
            }
        }
        catch (CsvHelperException)
        {
            return WireGuardErrors.Enrollment.MissingColumns;
        }

        var headers = (csv.HeaderRecord ?? [])
            .Select(h => h.Trim().ToLowerInvariant())
            .ToHashSet();
        if (!headers.Contains("name") || !headers.Contains("email"))
        {
            return WireGuardErrors.Enrollment.MissingColumns;
        }

        var rows = new List<ParsedEnrollmentRow>();
        while (csv.Read())
        {
            if (rows.Count >= MaxRows)
            {
                return WireGuardErrors.Enrollment.TooManyRows;
            }

            var row = new EnrollmentRow(
                Name: Field(csv, "name"),
                Email: Field(csv, "email"),
                Department: Field(csv, "department"),
                DeviceType: Field(csv, "devicetype"),
                AssignedAddress: Field(csv, "assignedaddress"),
                AllowedIps: SplitAllowedIps(Field(csv, "allowedips")));

            // Skip blank lines (e.g. a trailing newline) so they don't surface as spurious errors.
            if (IsBlank(row))
            {
                continue;
            }

            rows.Add(new ParsedEnrollmentRow(csv.Parser.Row, row));
        }

        return rows.Count == 0
            ? WireGuardErrors.Enrollment.NoRows
            : Result.Success<IReadOnlyList<ParsedEnrollmentRow>>(rows);
    }

    public string? Validate(EnrollmentRow row, string networkCidr)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
        {
            return "Name is required.";
        }

        if (row.Name.Length > Peer.MaxNameLength)
        {
            return $"Name must be {Peer.MaxNameLength} characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(row.Email))
        {
            return "Email is required.";
        }

        if (!EmailRegex().IsMatch(row.Email))
        {
            return "Email is not a valid address.";
        }

        if (row.AssignedAddress is { } addr)
        {
            if (!TryParseHost(addr, out var ip))
            {
                return $"Assigned address '{addr}' is not a valid IPv4 address.";
            }

            if (IPNetwork.TryParse(networkCidr, out var net) && !net.Contains(ip))
            {
                return $"Assigned address '{addr}' is outside the network {networkCidr}.";
            }
        }

        foreach (var cidr in row.AllowedIps)
        {
            if (!IsValidCidrOrIp(cidr))
            {
                return $"Allowed IP '{cidr}' is not valid.";
            }
        }

        return null;
    }

    public string NormalizeAddress(string address) => address.Contains('/') ? address : $"{address}/32";

    private static bool IsBlank(EnrollmentRow row) =>
        string.IsNullOrEmpty(row.Name) && string.IsNullOrEmpty(row.Email) &&
        row.Department is null && row.DeviceType is null && row.AssignedAddress is null && row.AllowedIps.Count == 0;

    private static string? Field(CsvReader csv, string name)
    {
        if (csv.TryGetField<string>(name, out var value))
        {
            var trimmed = value?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }

        return null;
    }

    private static IReadOnlyList<string> SplitAllowedIps(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseHost(string value, out IPAddress ip)
    {
        ip = IPAddress.None;
        var host = value.Split('/')[0];
        return IPAddress.TryParse(host, out ip!) && ip.AddressFamily == AddressFamily.InterNetwork;
    }

    private static bool IsValidCidrOrIp(string value) =>
        IPNetwork.TryParse(value, out _) || IPAddress.TryParse(value.Split('/')[0], out _);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();
}
