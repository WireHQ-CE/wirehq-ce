using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace WireHQ.Api.Security;

/// <summary>
/// Resolves the real client address behind the reverse-proxy chain (HAProxy → host nginx → web nginx → API), and
/// keys the per-client rate limiter on it.
/// <para>
/// Each proxy appends the address it received from to <c>X-Forwarded-For</c>. The middleware walks that list from
/// the right and stops at the first hop it does <b>not</b> trust, so the result is the nearest address no trusted
/// proxy vouches for — the real client. Entries left of it were written by the client and are ignored, so a forged
/// <c>X-Forwarded-For</c> cannot choose the address. Two things must hold together, which is why they live here:
/// <see cref="ForwardedHeadersOptions.ForwardLimit"/> is lifted (the default of 1 reads only the last hop — always
/// the host nginx — so every client shared ONE rate-limit bucket), and only known proxies are trusted (lifting the
/// limit while trusting every hop would hand the client the leftmost value, defeating the limiter outright).
/// </para>
/// <para>
/// Trusted by default: loopback and the private ranges (RFC 1918 + IPv6 ULA) — the container network and the host
/// proxy. A proxy on a public address (e.g. a separate HAProxy box) must be added via
/// <c>ForwardedHeaders:KnownProxies</c>; until it is, the walk stops at that proxy, which fails safe (every client
/// shares its bucket, as before this fix) rather than open. Setting <c>ForwardedHeaders:KnownNetworks</c> replaces
/// the defaults — do that where end users themselves sit on private addresses (a LAN self-host), or a LAN client
/// could pose as anyone by sending the header.
/// </para>
/// </summary>
public static class ClientAddress
{
    public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";
    public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";

    /// <summary>Used when the request has no remote address at all (the in-memory test server).</summary>
    public const string UnknownPartition = "anonymous";

    public static readonly IReadOnlyList<string> DefaultTrustedNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    /// <summary>
    /// The forwarded-headers options for the proxy chain. Throws at boot on a malformed network or address, so a
    /// typo cannot silently fall back to a wider or narrower trust set than the operator wrote.
    /// </summary>
    public static ForwardedHeadersOptions BuildForwardedHeadersOptions(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Walk the whole chain; the trust check below is what stops it.
            ForwardLimit = null,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var configuredNetworks = Split(configuration[KnownNetworksKey]);
        foreach (var network in configuredNetworks.Count > 0 ? configuredNetworks : DefaultTrustedNetworks)
        {
            options.KnownIPNetworks.Add(ParseNetwork(network));
        }

        foreach (var proxy in Split(configuration[KnownProxiesKey]))
        {
            options.KnownProxies.Add(IPAddress.TryParse(proxy, out var address)
                ? address
                : throw new InvalidOperationException($"{KnownProxiesKey}: '{proxy}' is not an IP address."));
        }

        return options;
    }

    /// <summary>
    /// The rate-limit partition for a request's (already resolved) client address. IPv4-mapped IPv6 is folded to
    /// IPv4 so one client cannot hold two buckets, and IPv6 is grouped by <b>/64</b> — the smallest block an ISP
    /// hands a single subscriber, who could otherwise rotate through 2^64 addresses to dodge the limit.
    /// </summary>
    public static string RateLimitPartition(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return UnknownPartition;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>A human-readable summary of the trust set, for the boot log.</summary>
    public static string Describe(ForwardedHeadersOptions options) =>
        string.Join(", ", options.KnownIPNetworks.Select(n => n.ToString())
            .Concat(options.KnownProxies.Select(p => p.ToString())));

    /// <summary>
    /// Parses a CIDR network, refusing a non-canonical base (host bits set, e.g. <c>10.0.0.5/8</c>): it is ambiguous
    /// whether one host or the whole block was meant, and <see cref="IPNetwork.TryParse(string?, out IPNetwork)"/>
    /// accepts it silently — so a typo would quietly widen or narrow the trusted range.
    /// </summary>
    private static IPNetwork ParseNetwork(string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash > 0
            && IPAddress.TryParse(cidr[..slash], out var written)
            && IPNetwork.TryParse(cidr, out var network)
            && HasNoHostBits(written, network.PrefixLength))
        {
            return network;
        }

        throw new InvalidOperationException(
            $"{KnownNetworksKey}: '{cidr}' is not a canonical CIDR network (e.g. 10.0.0.0/8).");
    }

    private static bool HasNoHostBits(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
        {
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
