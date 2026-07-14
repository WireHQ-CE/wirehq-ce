using WireHQ.Application.Updates;
using WireHQ.Licensing;

namespace WireHQ.Api.Updates;

/// <summary>
/// Fetches + verifies the WireHQ update manifest for a Community Edition install (docs/30 U-3/U-4). The fetch is
/// a plain <b>anonymous GET</b> of a static document with <b>zero identifiers</b> — no fingerprint, no version, no
/// custom headers or query — so it is a version check, not an install-tracking beacon (explicitly NOT the
/// licensing client's POST-with-fingerprint shape, only its fail-soft error handling). The response is a signed
/// PASETO token, verified locally against the pinned update public key (<c>Updates:PublicKey</c>, baked into the
/// CE compose); an unreachable host / bad signature / missing key all return null — treated as "no trustworthy
/// manifest", never a false all-clear. CE-ONLY (overlay-added).
/// </summary>
public sealed class SignedManifestClient(HttpClient httpClient, IConfiguration configuration, ILogger<SignedManifestClient> logger)
{
    public async Task<UpdateManifest?> FetchAsync(CancellationToken cancellationToken)
    {
        var url = configuration["Updates:ManifestUrl"];
        var publicKey = configuration["Updates:PublicKey"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(publicKey))
        {
            return null; // Not configured (e.g. WireHQ has not published a signing key yet) → silently no-op.
        }

        try
        {
            var token = await httpClient.GetStringAsync(url, cancellationToken);
            if (UpdateManifestCodec.TryVerify(token, publicKey, out var manifest))
            {
                return manifest;
            }

            logger.LogWarning("Update manifest failed signature verification; ignoring.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Update manifest fetch failed.");
            return null;
        }
    }
}
