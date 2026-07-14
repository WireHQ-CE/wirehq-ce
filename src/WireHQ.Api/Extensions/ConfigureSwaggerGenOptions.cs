using System.Text.RegularExpressions;
using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace WireHQ.Api.Extensions;

/// <summary>
/// Shapes the generated OpenAPI documents (docs/27-openapi-reference.md, O-5): one document per discovered API
/// version (a future v2 documents itself), both production auth schemes (a JWT session bearer and an
/// <c>X-Api-Key</c> API key — either satisfies an endpoint), the XML doc comments from the Api and Application
/// assemblies, an inclusion rule that keeps machine-only surfaces (the agent data plane, health probes) out of
/// the customer document, and a scrub filter that strips maintainer doc-references from the customer-facing
/// text. Registered as <see cref="IConfigureOptions{SwaggerGenOptions}"/> because the version list comes from
/// the DI-resolved <see cref="IApiVersionDescriptionProvider"/>, which isn't available at
/// <c>AddSwaggerGen(...)</c> time.
/// </summary>
public sealed class ConfigureSwaggerGenOptions(IApiVersionDescriptionProvider provider) : IConfigureOptions<SwaggerGenOptions>
{
    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, new OpenApiInfo
            {
                Title = "WireHQ API",
                Version = description.ApiVersion.ToString(),
                Description =
                    "The WireHQ management API. Authenticate every request with either a user session " +
                    "(`Authorization: Bearer <JWT>`) or a scoped API key (`X-Api-Key: whq_…`, also accepted as " +
                    "`Authorization: Bearer whq_…`) — keys are minted under Settings → API keys and carry " +
                    "permission-scoped access only. Errors follow RFC 9457 Problem Details; the machine-stable " +
                    "contract is the `code` field (e.g. `validation_error`, `insufficient_permission`), while " +
                    "`title`/`detail` are human text and may change.",
            });
        }

        // Versioned controllers carry an explorer group ("v1") and sort themselves. Minimal-API endpoints carry
        // none — admit those only into the document whose /api/{version}/ prefix their path carries, so the
        // WireGuard/Orchestration module endpoints land in v1 where they belong while the agent mTLS data plane
        // (/agent/v1/*) and the health probes never enter the customer document, and a future v2 doc won't
        // re-list every v1 minimal API.
        options.DocInclusionPredicate((documentName, apiDescription) =>
            // Never list the platform / super-admin surface (api/v{n}/platform/*) in the customer-facing
            // reference: those endpoints are Super-Admin-only (IPlatformRequest) and are not part of the API a
            // tenant consumes. Runtime authz already 403s the calls, but the *document* must not disclose the
            // surface (paths, schemas, "requires Super-Admin" descriptions) to a normal org admin or API key.
            // (In the CE these controllers are stripped entirely, so this is a no-op there.)
            apiDescription.RelativePath?.Contains("/platform/", StringComparison.OrdinalIgnoreCase) != true
            && (apiDescription.GroupName == documentName
                || (apiDescription.GroupName is null
                    && apiDescription.RelativePath?.StartsWith($"api/{documentName}/", StringComparison.OrdinalIgnoreCase) == true)));

        var bearer = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "A user-session access token from `POST /api/v1/auth/login`.",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
        };
        var apiKey = new OpenApiSecurityScheme
        {
            Name = "X-Api-Key",
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Description = "A scoped organization API key (`whq_…`) from Settings → API keys.",
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" },
        };

        options.AddSecurityDefinition("Bearer", bearer);
        options.AddSecurityDefinition("ApiKey", apiKey);
        // Two separate requirements = either scheme satisfies an endpoint (OR, matching the smart
        // JWT-or-API-key runtime scheme) — one requirement listing both would mean AND.
        options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
        options.AddSecurityRequirement(new OpenApiSecurityRequirement { [apiKey] = [] });

        // The Application XML lands in the Api output because documentation files flow with project
        // references; guard anyway so a build shape change degrades the docs, not the boot.
        foreach (var assembly in new[] { "WireHQ.Api.xml", "WireHQ.Application.xml" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, assembly);
            if (File.Exists(path))
            {
                options.IncludeXmlComments(path, includeControllerXmlComments: true);
            }
        }

        options.DocumentFilter<InternalDocReferenceFilter>();
    }
}

/// <summary>
/// Strips maintainer doc-references — <c>docs/…</c> / <c>ADR-…</c> pointers — from the customer-facing
/// document. The XML comments were written for people working in this repo; the generated reference is read by
/// API consumers who can't follow those pointers. Two passes: whole parenthetical asides that <i>contain</i> a
/// reference (the common form, e.g. <c>(change review, docs/22 §8)</c>) are removed entirely; any bare token
/// that survives is then dropped. (docs/27-openapi-reference.md, O-5)
/// </summary>
public sealed partial class InternalDocReferenceFilter : IDocumentFilter
{
    // A parenthetical (no nested parens) that mentions a doc reference anywhere inside it — not only at its start.
    [GeneratedRegex(@"\s*\([^()]*(?:docs/|ADR-)[^()]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceParenthetical();

    // A bare reference token left outside any parenthetical (e.g. a trailing "… see docs/15 §4").
    [GeneratedRegex(@"\s*(?:see\s+)?(?:docs/[\w.\-/]+(?:\s*§[\d.]+)?|ADR-\d+(?:\s*§[\d.]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceToken();

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var tag in document.Tags ?? [])
        {
            tag.Description = Scrub(tag.Description);
        }

        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in path.Operations.Values)
            {
                operation.Summary = Scrub(operation.Summary);
                operation.Description = Scrub(operation.Description);
                foreach (var parameter in operation.Parameters ?? [])
                {
                    parameter.Description = Scrub(parameter.Description);
                }

                if (operation.RequestBody is { } requestBody)
                {
                    requestBody.Description = Scrub(requestBody.Description);
                }

                if (operation.Responses is { } responses)
                {
                    foreach (var response in responses.Values)
                    {
                        response.Description = Scrub(response.Description);
                    }
                }
            }
        }

        foreach (var schema in document.Components?.Schemas.Values ?? [])
        {
            schema.Description = Scrub(schema.Description);
            foreach (var property in schema.Properties?.Values ?? [])
            {
                property.Description = Scrub(property.Description);
            }
        }
    }

    private static string? Scrub(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var scrubbed = ReferenceParenthetical().Replace(text, string.Empty);
        scrubbed = ReferenceToken().Replace(scrubbed, string.Empty);
        return scrubbed.Trim();
    }
}
