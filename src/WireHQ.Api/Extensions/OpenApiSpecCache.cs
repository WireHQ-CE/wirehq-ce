using System.Collections.Concurrent;
using System.Text;
using Asp.Versioning.ApiExplorer;
using Microsoft.OpenApi.Writers;
using Swashbuckle.AspNetCore.Swagger;

namespace WireHQ.Api.Extensions;

/// <summary>
/// Serves the generated OpenAPI document from a per-name cache (docs/27-openapi-reference.md, O-7).
/// Swashbuckle rebuilds the whole document on every <c>GetSwagger</c> call — it has no memoisation — so an
/// authenticated caller looping the endpoint would otherwise burn real CPU regenerating a document that only
/// changes per deploy. Only the <b>known</b> API-version document names are cacheable (the version explorer is
/// the allow-list), so an arbitrary or wrong-case name can neither grow the dictionary without bound nor poison
/// the real document; each name's serialized JSON is built lazily on first request and held for the process
/// lifetime, and a build failure is never cached (a transient fault must not turn into a permanent 500).
/// </summary>
public sealed class OpenApiSpecCache(IApiVersionDescriptionProvider provider)
{
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);

    public IResult Serve(HttpContext httpContext, ISwaggerProvider swaggerProvider, string documentName)
    {
        // Case-sensitive allow-list: Swashbuckle's own SwaggerDocs lookup is case-sensitive and the registered
        // names are the lowercase group names ("v1"), so an unknown or mis-cased name is a clean 404 with no
        // dictionary insert (defeats both the cache-poisoning and the unbounded-growth vectors).
        if (!IsKnownDocument(documentName))
        {
            return Results.NotFound();
        }

        // GetOrAdd builds at most once per known name; if generation throws it propagates (a 500) but nothing
        // is cached, so the next request retries — a transient fault can't become permanent.
        var json = _documents.GetOrAdd(documentName, name => Build(swaggerProvider, name));

        // Private: the document is auth-gated; a shared cache must not hold it. Five minutes just keeps a
        // busy reference page from re-downloading a few hundred KB per navigation.
        httpContext.Response.Headers.CacheControl = "private, max-age=300";
        return Results.Text(json, "application/json", Encoding.UTF8);
    }

    private bool IsKnownDocument(string documentName) =>
        provider.ApiVersionDescriptions.Any(d => string.Equals(d.GroupName, documentName, StringComparison.Ordinal));

    private static string Build(ISwaggerProvider swaggerProvider, string documentName)
    {
        var document = swaggerProvider.GetSwagger(documentName);
        using var writer = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(writer));
        return writer.ToString();
    }
}
