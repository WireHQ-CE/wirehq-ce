using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Features.Modules;

namespace WireHQ.Infrastructure.Modules;

/// <summary>
/// The HTTP implementation of <see cref="ILicensingClient"/> (docs/29-ce-marketplace-modules.md M-7): POSTs to
/// the hosted licensing service at <c>{Modules:LicensingBaseUrl}/api/v1/licensing/{activate,verify,deactivate}</c>
/// (default <c>https://licensing.wirehq.net</c>). The destination is operator-configured (not tenant-supplied),
/// so — unlike the webhook sender — no SSRF guard is needed. Every call is fail-soft: a transport error or a
/// non-success status maps to an <c>Unavailable</c> outcome and is logged, never thrown, so the control plane
/// never faults on a licensing outage. CE-ONLY (overlay-added).
/// </summary>
public sealed class HttpLicensingClient(HttpClient httpClient, IConfiguration configuration, ILogger<HttpLicensingClient> logger)
    : ILicensingClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string? CeVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();

    private string BaseUrl =>
        (configuration["Modules:LicensingBaseUrl"] ?? "https://licensing.wirehq.net").TrimEnd('/');

    public async Task<LicensingActivation> ActivateAsync(string licenceKey, string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"{BaseUrl}/api/v1/licensing/activate",
                new { licenceKey, fingerprint, ceVersion = CeVersion }, Json, cancellationToken);

            // A 409 is either a revoked licence or a slot held by another install — the ProblemDetails `code`
            // distinguishes them, so the operator gets the right guidance (a revoked licence has no slot to free).
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var code = await ReadProblemCodeAsync(response, cancellationToken);
                return new LicensingActivation(
                    code == "marketplace.activation.revoked" ? LicensingOutcome.Revoked : LicensingOutcome.SlotTaken,
                    null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Licensing activate returned {Status}.", (int)response.StatusCode);
                return new LicensingActivation(LicensingOutcome.Unavailable, null, null);
            }

            var body = await response.Content.ReadFromJsonAsync<ActivationResponse>(Json, cancellationToken);
            return body is null || string.IsNullOrWhiteSpace(body.ActivationToken)
                ? new LicensingActivation(LicensingOutcome.Unavailable, null, null)
                : new LicensingActivation(LicensingOutcome.Activated, body.ActivationToken, body.GraceEndsUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Licensing activate call failed.");
            return new LicensingActivation(LicensingOutcome.Unavailable, null, null);
        }
    }

    public async Task<LicensingVerification> VerifyAsync(string activationToken, string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"{BaseUrl}/api/v1/licensing/verify",
                new { activationToken, fingerprint, ceVersion = CeVersion }, Json, cancellationToken);

            // A reached-but-rejected token (400 — the stored token has expired, since its exp IS the grace
            // boundary) is distinct from a transport outage: the caller re-activates to self-heal (M-7), whereas
            // an outage leaves the licence in offline grace. A 5xx is treated as an outage (retry next pass).
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return new LicensingVerification(LicensingVerifyOutcome.Expired, null, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Licensing verify returned {Status}.", (int)response.StatusCode);
                return new LicensingVerification(LicensingVerifyOutcome.Unavailable, null, null, null);
            }

            var body = await response.Content.ReadFromJsonAsync<VerificationResponse>(Json, cancellationToken);
            if (body is null)
            {
                return new LicensingVerification(LicensingVerifyOutcome.Unavailable, null, null, null);
            }

            return string.Equals(body.Status, "revoked", StringComparison.OrdinalIgnoreCase)
                ? new LicensingVerification(LicensingVerifyOutcome.Revoked, null, null, body.RevokeReason)
                : new LicensingVerification(LicensingVerifyOutcome.Active, body.ActivationToken, body.GraceEndsUtc, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Licensing verify call failed.");
            return new LicensingVerification(LicensingVerifyOutcome.Unavailable, null, null, null);
        }
    }

    public async Task<LicensingDeactivateOutcome> DeactivateAsync(string activationToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"{BaseUrl}/api/v1/licensing/deactivate", new { activationToken }, Json, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return LicensingDeactivateOutcome.Freed;
            }

            // A 409 is a hard refusal that keeps the slot bound (e.g. the self-serve move limit, D-4) — the caller
            // must keep the local row so the operator can retry; anything else is treated as best-effort.
            logger.LogWarning("Licensing deactivate returned {Status}.", (int)response.StatusCode);
            return response.StatusCode == HttpStatusCode.Conflict
                ? LicensingDeactivateOutcome.Refused
                : LicensingDeactivateOutcome.Unavailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Licensing deactivate call failed.");
            return LicensingDeactivateOutcome.Unavailable;
        }
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(Json, cancellationToken);
            return problem?.Code;
        }
        catch
        {
            return null;
        }
    }

    // The subset of the hosted service's response we consume (System.Text.Json Web defaults → camelCase).
    private sealed record ActivationResponse(string ActivationToken, DateTimeOffset GraceEndsUtc);

    private sealed record VerificationResponse(string Status, string? ActivationToken, DateTimeOffset? GraceEndsUtc, string? RevokeReason);

    // The RFC 9457 error body carries the domain error code as a `code` extension member (ApiControllerBase).
    private sealed record ProblemBody(string? Code);
}
