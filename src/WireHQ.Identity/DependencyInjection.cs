using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Application.Abstractions.Security;
using WireHQ.Identity.Jwt;
using WireHQ.Identity.Mfa;
using WireHQ.Identity.Passwords;
using WireHQ.Identity.Protection;

namespace WireHQ.Identity;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the security adapters and binds their options. The HTTP authentication pipeline
    /// (JwtBearer + Authorization) is configured by the API host, which owns the request pipeline
    /// and has the ASP.NET framework reference — this keeps the Identity library free of host concerns.
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<SecretProtectionOptions>(configuration.GetSection(SecretProtectionOptions.SectionName));

        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITotpService, TotpService>();
        services.AddSingleton<IRecoveryCodeService, RecoveryCodeService>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        return services;
    }
}
