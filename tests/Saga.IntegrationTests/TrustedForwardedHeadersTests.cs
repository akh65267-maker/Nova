
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using TrustedForwardedHeaders = Observability.TrustedForwardedHeaders;

namespace Saga.IntegrationTests;

/// <summary>
/// Which client address a service ends up with, given who connected and what the X-Forwarded-For
/// header says. A client must never be able to pick its own address by sending that header.
/// </summary>
public class TrustedForwardedHeadersTests
{
    // Runs the real middleware in a tiny host. "X-Test-Remote" stands in for the TCP peer, since a
    // test server has no real connection to read an address from.
    private static async Task<string> AddressSeenAsync(string? knownNetworks, string peer, string? forwardedFor)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (knownNetworks is not null)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks:0"] = knownNetworks });

        var trusted = TrustedForwardedHeaders.Configure(builder.Services, builder.Configuration);
        await using var app = builder.Build();

        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Remote"]!);
            return next();
        });
        if (trusted)
            app.UseForwardedHeaders();
        app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress!.ToString()));

        await app.StartAsync();
        var client = app.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Test-Remote", peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task WithNothingConfigured_TheHeaderIsIgnored()
    {
        Assert.Equal("172.18.0.5", await AddressSeenAsync(knownNetworks: null, peer: "172.18.0.5", forwardedFor: "203.0.113.9"));
    }

    [Fact]
    public async Task FromATrustedNetwork_TheForwardedAddressIsUsed()
    {
        Assert.Equal("203.0.113.9", await AddressSeenAsync("172.16.0.0/12", peer: "172.18.0.5", forwardedFor: "203.0.113.9"));
    }

    [Fact]
    public async Task FromAnAddressOutsideTheTrustedNetworks_TheHeaderIsIgnored()
    {
        // Someone reaching the service directly, bypassing the gateway, cannot claim another address.
        Assert.Equal("198.51.100.7", await AddressSeenAsync("172.16.0.0/12", peer: "198.51.100.7", forwardedFor: "203.0.113.9"));
    }

    [Fact]
    public async Task OnlyTheLastEntryCounts_SoAClientCannotChooseItsOwnAddress()
    {
        // The client sent "6.6.6.6"; the trusted proxy appended the address it actually saw.
        Assert.Equal("203.0.113.9", await AddressSeenAsync("172.16.0.0/12", peer: "172.18.0.5", forwardedFor: "6.6.6.6, 203.0.113.9"));
    }

    [Theory]
    [InlineData("not-a-network")]
    [InlineData("172.16.0.0")]
    [InlineData("172.16.0.0/abc")]
    public void AMalformedNetwork_StopsTheServiceStarting_InsteadOfTrustingNothingSilently(string entry)
    {
        Assert.Throws<InvalidOperationException>(() => TrustedForwardedHeaders.ParseNetworks(new[] { entry }));
    }
}
