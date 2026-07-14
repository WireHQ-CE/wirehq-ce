using System.Net;
using System.Net.Sockets;

namespace WireHQ.Domain.Webhooks;

/// <summary>
/// Classifies a resolved IP address as a <b>disallowed webhook destination</b> — loopback, private (RFC1918),
/// carrier-grade NAT, link-local (incl. the <c>169.254.169.254</c> cloud-metadata endpoint), unique-local IPv6, or
/// unspecified (docs/26-api-keys-webhooks.md §11). Used by the sender's <c>ConnectCallback</c> to block SSRF at
/// connect time — after DNS resolution, so a name that resolves to an internal address can't slip through (and
/// DNS-rebinding is defeated because the vetted address is the one actually connected to).
/// </summary>
public static class WebhookAddressGuard
{
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                127 => true,                                  // 127.0.0.0/8 (also caught by IsLoopback)
                169 when b[1] == 254 => true,                 // 169.254.0.0/16 link-local incl. metadata
                172 when b[1] is >= 16 and <= 31 => true,     // 172.16.0.0/12
                192 when b[1] == 168 => true,                 // 192.168.0.0/16
                100 when b[1] is >= 64 and <= 127 => true,    // 100.64.0.0/10 CGNAT
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local (fe80::/10), site-local (deprecated fec0::/10), and unique-local (fc00::/7).
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return true; // unknown address family — fail closed
    }
}
