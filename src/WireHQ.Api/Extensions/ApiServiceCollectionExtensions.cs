using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;
using WireHQ.Api.Middleware;
using WireHQ.Api.Security;
using WireHQ.Application.Abstractions;
using WireHQ.Shared.Observability;

namespace WireHQ.Api.Extensions;

public static class ApiServiceCollectionExtensions
{
    public const string AuthRateLimitPolicy = "auth";

    public static IServiceCollection AddApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();

        // HttpContext-backed implementations of the Application ports.
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<RequestTenantContext>());
        // Same scoped instance, exposed as settable so background/system work (the deployment-job
        // dispatcher, reconciler, agent gateway) can establish a tenant when there's no request.
        services.AddScoped<ISettableTenantContext>(sp => sp.GetRequiredService<RequestTenantContext>());
        services.AddScoped<IRequestContext, RequestContext>();

        // Suppress the automatic [ApiController] 400 so our FluentValidation pipeline owns
        // validation responses (consistent ProblemDetails + stable codes).
        services.AddControllers().ConfigureApiBehaviorOptions(o => o.SuppressModelStateInvalidFilter = true);

        // Accept + emit application/scim+json (what SCIM 2.0 clients send/expect) via the JSON formatters, so the
        // SCIM endpoints (docs/21 §11) work with real IdPs. PostConfigure runs after the JSON formatters are added.
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            foreach (var input in options.InputFormatters.OfType<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter>())
            {
                input.SupportedMediaTypes.Add("application/scim+json");
            }

            foreach (var output in options.OutputFormatters.OfType<Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonOutputFormatter>())
            {
                output.SupportedMediaTypes.Add("application/scim+json");
            }
        });
        // Own the ProblemDetails `traceId` so every problem the framework writes (minimal-API `Results.Problem`,
        // automatic 4xx/5xx) carries the same correlation reference as the X-Correlation-Id header + audit + logs:
        // the W3C trace id (not the default full traceparent). Runs last, so it wins. (ADR-030)
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions["traceId"] = CorrelationId.Current() ?? context.HttpContext.TraceIdentifier);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddWireHqAuthentication(configuration);

        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

        services.AddCors(configuration);
        services.AddRateLimiting(configuration);
        services.AddSwagger();

        return services;
    }

    private static void AddCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // Expose the correlation reference so a cross-origin SPA (a separate VITE_API_BASE_URL
                // origin) can read it for support + error reporting; it's not a CORS-safelisted header
                // (ADR-030). The default same-origin ingress doesn't need this, but it's correct to set.
                .WithExposedHeaders("X-Correlation-Id")
                .AllowCredentials()));
    }

    private static void AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var globalPerMinute = configuration.GetValue("RateLimiting:GlobalPermitPerMinute", 300);
        var authPerMinute = configuration.GetValue("RateLimiting:AuthPermitPerMinute", 10);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Per-IP global limit.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = globalPerMinute, Window = TimeSpan.FromMinutes(1) }));

            // Stricter limit for auth endpoints (login/register/refresh/forgot).
            options.AddPolicy(AuthRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    private static void AddSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        // The document's real shaping lives in ConfigureSwaggerGenOptions (per-version docs, both auth schemes,
        // XML comments, inclusion + scrub filters) — registered as IConfigureOptions because it needs the
        // DI-resolved API-version explorer. The cache serves the serialized document (docs/27, O-7).
        services.AddSwaggerGen();
        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerGenOptions>();
        services.AddSingleton<OpenApiSpecCache>();
    }
}
