using System.Security.Cryptography;

namespace WireHQ.Domain.Webhooks;

/// <summary>
/// Generates a webhook endpoint's HMAC signing secret (docs/26-api-keys-webhooks.md §7): a high-entropy, URL-safe
/// token prefixed <c>whsec_</c>, shown to the operator <b>once</b>. Unlike an API key it is stored <b>reversibly</b>
/// (encrypted via <c>ISecretProtector</c>, not hashed) so the sender can re-derive the HMAC on every delivery.
/// </summary>
public static class WebhookSecret
{
    public const string Prefix = "whsec_";

    public static string Generate() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
