namespace WireHQ.Modules.Orchestration.Gateway;

/// <summary>
/// Configures the agent mTLS gateway — the dedicated Kestrel listener that terminates agent client
/// certificates and serves <c>/agent/v1/*</c>. Disabled by default so existing single-listener (:8080,
/// JWT) deployments are unchanged on upgrade; opt in per environment. When enabled in a non-Development
/// environment a server certificate is required (Development auto-generates an ephemeral one). (ADR-028)
/// </summary>
public sealed class AgentGatewayOptions
{
    public const string SectionName = "AgentGateway";

    /// <summary>Whether the dedicated agent listener is bound + <c>/agent/v1/*</c> served. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>The TCP port the agent mTLS listener binds (HAProxy TCP-passes this through in prod).</summary>
    public int Port { get; set; } = 28443;

    /// <summary>Path to the listener's server certificate (PKCS#12/PFX). Required outside Development.</summary>
    public string? ServerCertificatePath { get; set; }

    /// <summary>Password for <see cref="ServerCertificatePath"/>, if any.</summary>
    public string? ServerCertificatePassword { get; set; }
}
