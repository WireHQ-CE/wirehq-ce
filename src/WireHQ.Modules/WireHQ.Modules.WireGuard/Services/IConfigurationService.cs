namespace WireHQ.Modules.WireGuard.Services;

/// <summary>
/// The Configuration layer (docs/11-wireguard-module.md §6). Renders canonical <c>wg-quick</c>
/// configuration from materialized inputs (no DB access — pure + testable) and computes a content
/// checksum for versioning. The caller gathers/decrypts the inputs and persists versions.
/// </summary>
public interface IConfigurationService
{
    /// <summary>Renders a client (peer) <c>wg-quick</c> config.</summary>
    string RenderPeerConfig(PeerConfigInput input);

    /// <summary>Renders a server (instance) <c>wg-quick</c> config with one [Peer] block per peer.</summary>
    string RenderInstanceConfig(InstanceConfigInput input);

    /// <summary>SHA-256 (hex) of the plaintext config, for version integrity/diff.</summary>
    string Checksum(string content);
}

public sealed record PeerConfigInput(
    string PrivateKey,
    string Address,
    IReadOnlyList<string> Dns,
    int? Mtu,
    string ServerPublicKey,
    string? PresharedKey,
    string? Endpoint,
    IReadOnlyList<string> AllowedIps,
    int? PersistentKeepalive);

public sealed record InstancePeerEntry(string PublicKey, string? PresharedKey, string AssignedAddress);

/// <summary>
/// Inputs for a server (instance) config. <see cref="PrivateKey"/> is null for an
/// <c>AgentManaged</c> instance — WireHQ never holds the interface private key, so the rendered
/// <c>[Interface]</c> omits the <c>PrivateKey</c> line and the agent injects its locally-held key
/// before bringing the interface up. (ADR-020/028)
/// </summary>
public sealed record InstanceConfigInput(
    string? PrivateKey,
    string InterfaceAddress,
    int ListenPort,
    int? Mtu,
    IReadOnlyList<InstancePeerEntry> Peers);
