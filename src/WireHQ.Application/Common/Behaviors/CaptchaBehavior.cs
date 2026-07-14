using MediatR;
using Microsoft.EntityFrameworkCore;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Abstractions.Persistence;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Application.Common.Messaging;
using WireHQ.Shared.Results;

namespace WireHQ.Application.Common.Behaviors;

/// <summary>
/// Site-wide CAPTCHA gate. For requests marked <see cref="ICaptchaProtected"/> (the public auth use
/// cases), if Turnstile is enabled + configured in platform settings, the request must carry a token
/// that Cloudflare confirms — otherwise the request short-circuits with a failure Result (→ 403). All
/// other requests pass straight through with no database hit, so this is free for normal traffic.
/// (docs/04-security.md)
/// </summary>
public sealed class CaptchaBehavior<TRequest, TResponse>(
    IApplicationDbContext dbContext,
    ISecretProtector secretProtector,
    ITurnstileVerifier verifier,
    IRequestContext requestContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private static readonly Error Required =
        Error.Forbidden("captcha.required", "Captcha verification is required.");

    private static readonly Error Failed =
        Error.Forbidden("captcha.failed", "Captcha verification failed. Please try again.");

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICaptchaProtected captcha)
        {
            return await next();
        }

        // The single platform-settings row drives the toggle. Unconfigured (no secret) ⇒ fail-open,
        // so a half-set-up Turnstile can never lock everyone out of the auth pages.
        var settings = await dbContext.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings is not { TurnstileEnabled: true, TurnstileConfigured: true })
        {
            return await next();
        }

        if (string.IsNullOrWhiteSpace(captcha.TurnstileToken))
        {
            return ResultFactory.Failure<TResponse>(Required);
        }

        var secret = secretProtector.Unprotect(settings.TurnstileSecretCiphertext!);
        var verified = await verifier.VerifyAsync(secret, captcha.TurnstileToken, requestContext.IpAddress, cancellationToken);

        return verified ? await next() : ResultFactory.Failure<TResponse>(Failed);
    }
}
