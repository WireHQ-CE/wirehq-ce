using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using WireHQ.Modules.Orchestration.Gateway;

namespace WireHQ.Api.Extensions;

/// <summary>
/// Wires the agent mTLS gateway's dedicated Kestrel listener in the host (the composition root owns the
/// request pipeline). When enabled it binds a second HTTPS listener that requests + accepts ANY client
/// certificate — the <c>AgentCertificate</c> scheme does the real authentication by fingerprint, since the
/// per-org CAs are created lazily and cannot be pinned at the TLS layer. The main JWT (:8080) listener is
/// re-declared here because a code-level <c>Listen()</c> overrides the env-driven URLs. (ADR-028)
/// </summary>
public static class AgentGatewaySetup
{
    public static void ConfigureAgentGateway(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AgentGatewayOptions.SectionName).Get<AgentGatewayOptions>()
            ?? new AgentGatewayOptions();
        if (!options.Enabled)
        {
            return;
        }

        var mainHttpPort = ResolveMainHttpPort(builder.Configuration);
        var serverCertificate = ResolveServerCertificate(options, builder.Environment);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Preserve the JWT API listener (a code Listen() supersedes ASPNETCORE_HTTP_PORTS).
            kestrel.ListenAnyIP(mainHttpPort);

            // The agent listener: client certs requested + accepted unconditionally; the app authenticates.
            kestrel.ListenAnyIP(options.Port, listen => listen.UseHttps(serverCertificate, https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.AllowAnyClientCertificate();
            }));
        });
    }

    private static int ResolveMainHttpPort(IConfiguration configuration)
    {
        // ASPNETCORE_HTTP_PORTS may carry a semicolon-separated list — take the first; default 8080.
        var ports = configuration["ASPNETCORE_HTTP_PORTS"];
        if (!string.IsNullOrWhiteSpace(ports))
        {
            var first = ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (int.TryParse(first, out var port))
            {
                return port;
            }
        }

        return 8080;
    }

    private static X509Certificate2 ResolveServerCertificate(AgentGatewayOptions options, IWebHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerCertificatePath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(options.ServerCertificatePath, options.ServerCertificatePassword);
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "AgentGateway:ServerCertificatePath is required when the agent gateway is enabled outside Development.");
        }

        return GenerateDevelopmentCertificate();
    }

    /// <summary>An ephemeral self-signed server cert for Development/test — never used in production.</summary>
    private static X509Certificate2 GenerateDevelopmentCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=wirehq-agent-gateway", key, HashAlgorithmName.SHA256);

        var subjectAltNames = new SubjectAlternativeNameBuilder();
        subjectAltNames.AddDnsName("localhost");
        subjectAltNames.AddDnsName("host.docker.internal");
        request.CertificateExtensions.Add(subjectAltNames.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        // Round-trip via PKCS#12 so Kestrel reliably owns a usable private key across platforms.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
