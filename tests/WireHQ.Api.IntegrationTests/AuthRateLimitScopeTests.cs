using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WireHQ.Api.Controllers.V1;
using WireHQ.Api.Extensions;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Which auth endpoints carry the strict brute-force limit, read from the real endpoint table (so class- and
/// action-level attributes are both accounted for). The SPA calls refresh/me/security-config on every page load;
/// when the limit was controller-wide, ~10 page views used up the budget and the next sign-in got a 429.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthRateLimitScopeTests(WireHqApiFactory factory)
{
    [Fact]
    public void Only_credential_and_email_sending_auth_endpoints_carry_the_strict_limit()
    {
        var authEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo == typeof(AuthController))
            .ToList();

        var strict = authEndpoints
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == ApiServiceCollectionExtensions.AuthRateLimitPolicy)
            .Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>()!.ActionName);

        authEndpoints.Should().NotBeEmpty();
        strict.Should().BeEquivalentTo(
            "Setup", "Register", "Login", "VerifyMfa", "ForgotPassword", "ResetPassword", "VerifyEmail", "ResendVerification");
    }
}
