using System.Security.Cryptography;
using System.Text;

namespace WireHQ.Modules.WireGuard.Services;

/// <summary>Renders standard <c>wg-quick</c> configuration. Output is interoperable with the WireGuard clients.</summary>
public sealed class ConfigurationService : IConfigurationService
{
    public string RenderPeerConfig(PeerConfigInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        sb.AppendLine($"PrivateKey = {input.PrivateKey}");
        sb.AppendLine($"Address = {input.Address}");
        if (input.Dns.Count > 0)
        {
            sb.AppendLine($"DNS = {string.Join(", ", input.Dns)}");
        }

        if (input.Mtu is { } mtu)
        {
            sb.AppendLine($"MTU = {mtu}");
        }

        sb.AppendLine();
        sb.AppendLine("[Peer]");
        sb.AppendLine($"PublicKey = {input.ServerPublicKey}");
        if (!string.IsNullOrEmpty(input.PresharedKey))
        {
            sb.AppendLine($"PresharedKey = {input.PresharedKey}");
        }

        if (!string.IsNullOrEmpty(input.Endpoint))
        {
            sb.AppendLine($"Endpoint = {input.Endpoint}");
        }

        var allowed = input.AllowedIps.Count > 0 ? input.AllowedIps : ["0.0.0.0/0", "::/0"];
        sb.AppendLine($"AllowedIPs = {string.Join(", ", allowed)}");
        if (input.PersistentKeepalive is { } keepalive)
        {
            sb.AppendLine($"PersistentKeepalive = {keepalive}");
        }

        return sb.ToString();
    }

    public string RenderInstanceConfig(InstanceConfigInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        // AgentManaged instances render without a PrivateKey line — the agent injects its locally-held
        // key before `wg-quick up` (WireHQ never holds it). (ADR-028)
        if (!string.IsNullOrEmpty(input.PrivateKey))
        {
            sb.AppendLine($"PrivateKey = {input.PrivateKey}");
        }

        sb.AppendLine($"Address = {input.InterfaceAddress}");
        sb.AppendLine($"ListenPort = {input.ListenPort}");
        if (input.Mtu is { } mtu)
        {
            sb.AppendLine($"MTU = {mtu}");
        }

        foreach (var peer in input.Peers)
        {
            sb.AppendLine();
            sb.AppendLine("[Peer]");
            sb.AppendLine($"PublicKey = {peer.PublicKey}");
            if (!string.IsNullOrEmpty(peer.PresharedKey))
            {
                sb.AppendLine($"PresharedKey = {peer.PresharedKey}");
            }

            sb.AppendLine($"AllowedIPs = {peer.AssignedAddress}");
        }

        return sb.ToString();
    }

    public string Checksum(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
