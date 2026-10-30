using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireHQ.Api.Security;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// The client address behind the proxy chain (docs/14 finding #1), proven against the REAL ASP.NET
/// <see cref="ForwardedHeadersMiddleware"/> configured by <see cref="ClientAddress"/>. Production is
/// HAProxy → host nginx → web nginx → API; each hop appends to <c>X-Forwarded-For</c>, so the API sees the web
/// container as its peer. The defect this pins: the old setup read only the LAST hop (always the host nginx), so
/// every client shared one rate-limit bucket — and the obvious fix (read it all, trust everyone) lets a client
/// forge the address and walk straight past the limiter.
/// </summary>
public sealed class ClientAddressTests
{
    private const string WebContainer = "172.18.0.5"; // the API's TCP peer: the web container's nginx
    private const string HostNginx = "172.18.0.1";    // the docker bridge gateway, as the web nginx sees the host
    private const string Client = "203.0.113.7";
    private const string PublicHaproxy = "198.51.100.1";

    [Fact]
    public async Task The_real_client_is_resolved_through_trusted_private_hops()
    {
        var context = await ResolveAsync(WebContainer, $"{Client}, {HostNginx}");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(Client));
    }

    [Fact]
    public async Task A_dual_stack_peer_in_ipv4_mapped_form_is_still_a_trusted_hop()
    {
        // Kestrel listening on [::] reports an IPv4 peer as ::ffff:a.b.c.d — it must match 172.16.0.0/12.
        var context = await ResolveAsync($"::ffff:{WebContainer}", $"{Client}, {HostNginx}");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(Client));
    }

    [Fact]
    public async Task A_forged_x_forwarded_for_entry_cannot_choose_the_address()
    {
        // The client sent "X-Forwarded-For: 6.6.6.6"; the edge appended the address it really came from.
        var context = await ResolveAsync(WebContainer, $"6.6.6.6, {Client}, {HostNginx}");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(Client));
    }

    [Fact]
    public async Task A_direct_untrusted_peer_is_taken_as_is_and_its_header_ignored()
    {
        // e.g. a CE install whose web port faces the internet directly — but here the API itself is reached.
        var context = await ResolveAsync("198.51.100.9", "1.2.3.4");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("198.51.100.9"));
    }

    [Fact]
    public async Task An_unconfigured_public_proxy_fails_safe_at_the_proxy_never_open()
    {
        var chain = $"6.6.6.6, {Client}, {PublicHaproxy}, {HostNginx}";

        // Not configured: the walk stops at the public HAProxy — one shared bucket (the old behaviour), but no
        // client-chosen address.
        (await ResolveAsync(WebContainer, chain)).Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(PublicHaproxy));

        // Configured: it is trusted, and the real client comes through.
        var configured = await ResolveAsync(WebContainer, chain, (ClientAddress.KnownProxiesKey, PublicHaproxy));
        configured.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(Client));
    }

    [Fact]
    public async Task Configured_networks_replace_the_private_range_default()
    {
        // Trust only 10/8: the 172.18 web container is no longer a trusted hop, so nothing is forwarded.
        var context = await ResolveAsync(WebContainer, $"{Client}, {HostNginx}", (ClientAddress.KnownNetworksKey, "10.0.0.0/8"));

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(WebContainer));
    }

    [Fact]
    public async Task The_forwarded_scheme_is_honoured_so_cookies_stay_secure()
    {
        var context = await ResolveAsync(WebContainer, $"{Client}, {HostNginx}", proto: "https");

        context.Request.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task Different_clients_get_different_rate_limit_buckets()
    {
        var first = await ResolveAsync(WebContainer, $"203.0.113.7, {HostNginx}");
        var second = await ResolveAsync(WebContainer, $"203.0.113.8, {HostNginx}");

        ClientAddress.RateLimitPartition(first).Should().NotBe(ClientAddress.RateLimitPartition(second));
    }

    [Fact]
    public void Ipv6_clients_are_bucketed_per_64_so_rotating_addresses_does_not_escape_the_limit()
    {
        ClientAddress.RateLimitPartition(WithPeer("2001:db8:1:2::1"))
            .Should().Be(ClientAddress.RateLimitPartition(WithPeer("2001:db8:1:2:ffff:ffff:ffff:ffff")))
            .And.Be("2001:db8:1:2::/64");
        ClientAddress.RateLimitPartition(WithPeer("2001:db8:1:3::1"))
            .Should().NotBe(ClientAddress.RateLimitPartition(WithPeer("2001:db8:1:2::1")));
    }

    [Fact]
    public void An_ipv4_mapped_address_shares_the_ipv4_bucket()
    {
        ClientAddress.RateLimitPartition(WithPeer("::ffff:203.0.113.7")).Should().Be("203.0.113.7");
    }

    [Fact]
    public void No_remote_address_falls_into_the_unknown_bucket()
    {
        ClientAddress.RateLimitPartition(new DefaultHttpContext()).Should().Be(ClientAddress.UnknownPartition);
    }

    [Theory]
    [InlineData(ClientAddress.KnownNetworksKey, "10.0.0.0")]
    [InlineData(ClientAddress.KnownNetworksKey, "10.0.0.0/33")]
    [InlineData(ClientAddress.KnownNetworksKey, "not-a-network/8")]
    [InlineData(ClientAddress.KnownProxiesKey, "haproxy.internal")]
    public void A_malformed_trust_setting_fails_at_boot_rather_than_guessing(string key, string value)
    {
        var act = () => ClientAddress.BuildForwardedHeadersOptions(Config((key, value)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{value}*");
    }

    private static async Task<HttpContext> ResolveAsync(
        string peer, string forwardedFor, (string Key, string Value)? setting = null, string? proto = null)
    {
        var options = ClientAddress.BuildForwardedHeadersOptions(setting is { } s ? Config(s) : Config());
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));

        var context = WithPeer(peer);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (proto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = proto;
        }

        await middleware.Invoke(context);
        return context;
    }

    private static DefaultHttpContext WithPeer(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Scheme = "http";
        return context;
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
}
