using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WireHQ.Api.Middleware;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Unit-style coverage for <see cref="GlobalExceptionHandler"/>: an optimistic-concurrency conflict
/// (the xmin row-version guard, re-added with the concurrency tokens) becomes a 409 Conflict, while
/// any other unexpected exception stays a generic 500. Pure in-memory — no web host or database.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task DbUpdateConcurrencyException_maps_to_409_conflict()
    {
        var (status, code) = await HandleAsync(new DbUpdateConcurrencyException("row version mismatch"));

        status.Should().Be(StatusCodes.Status409Conflict);
        code.Should().Be("concurrency_conflict");
    }

    [Fact]
    public async Task Unexpected_exception_maps_to_500_internal()
    {
        var (status, code) = await HandleAsync(new InvalidOperationException("boom"));

        status.Should().Be(StatusCodes.Status500InternalServerError);
        code.Should().Be("internal_error");
    }

    private static async Task<(int Status, string? Code)> HandleAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        handled.Should().BeTrue();

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

        return (context.Response.StatusCode, code);
    }
}
