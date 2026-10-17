extern alias GatewayAlias;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Saga.IntegrationTests;

/// <summary>
/// Which caller address the gateway passes on to the services behind it. The services limit
/// sign-in attempts per caller; if the gateway only ever passed on its own neighbour's address
/// (the shop's server), every user would share one limit and a few failed sign-ins from anyone
/// would block everyone. A real proxy and a real stand-in service, so what is checked is what the
/// service behind the gateway actually receives.
/// </summary>
public class GatewayForwardedAddressTests : IAsyncLifetime
{
    private WebApplication _downstream = default!;
    private string _downstreamUrl = default!;
    private string? _lastForwardedFor;

    public async Task InitializeAsync()
    {
        // Stands in for UserService: it answers, and remembers what X-Forwarded-For it was sent.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _downstream = builder.Build();
        _downstream.Run(context =>
        {
            _lastForwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
            return context.Response.WriteAsync("ok");
        });
        await _downstream.StartAsync();
        _downstreamUrl = _downstream.Urls.First();
    }

    public async Task DisposeAsync() => await _downstream.DisposeAsync();

    // Plays the part of the connection: a test server has no peer address, so a header says who
    // "connected", set before anything else in the gateway's pipeline sees the request.
    private sealed class PeerAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Peer"]!);
                return nextMiddleware();
            });
            next(app);
        };
    }

    private async Task<string?> ForwardedForSeenDownstreamAsync(string? trustedNetworks, string peer, string? forwardedFor)
    {
        await using var factory = new WebApplicationFactory<GatewayAlias::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ReverseProxy:Clusters:users-cluster:Destinations:destination1:Address", _downstreamUrl);
            if (trustedNetworks is not null)
                builder.UseSetting("ForwardedHeaders:KnownNetworks:0", trustedNetworks);
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, PeerAddress>());
        });
        var client = factory.CreateClient();

        _lastForwardedFor = null;
        var request = new HttpRequestMessage(HttpMethod.Get, "/users/anything");
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return _lastForwardedFor;
    }

    [Fact]
    public async Task FromATrustedNetwork_TheServiceBehindIsToldTheRealCaller_NotTheProxyNextToTheGateway()
    {
        // The shop's server (172.18.0.5, inside the trusted Docker range) says its caller was 203.0.113.9.
        var seen = await ForwardedForSeenDownstreamAsync("172.16.0.0/12", peer: "172.18.0.5", forwardedFor: "203.0.113.9");

        Assert.Equal("203.0.113.9", seen);
    }

    [Fact]
    public async Task FromAnAddressThatIsNotTrusted_TheClaimIsIgnored_AndTheServiceSeesTheRealPeer()
    {
        // Someone reaching the gateway directly cannot choose the address the service rate-limits on.
        var seen = await ForwardedForSeenDownstreamAsync("172.16.0.0/12", peer: "198.51.100.7", forwardedFor: "203.0.113.9");

        Assert.Equal("198.51.100.7", seen);
    }

    [Fact]
    public async Task WithNothingConfigured_TheClaimIsIgnored()
    {
        var seen = await ForwardedForSeenDownstreamAsync(null, peer: "172.18.0.5", forwardedFor: "203.0.113.9");

        Assert.Equal("172.18.0.5", seen);
    }

    [Fact]
    public async Task OnlyTheLastEntryCounts_SoACallerCannotChooseItsOwnAddress()
    {
        var seen = await ForwardedForSeenDownstreamAsync("172.16.0.0/12", peer: "172.18.0.5", forwardedFor: "6.6.6.6, 203.0.113.9");

        Assert.Equal("203.0.113.9", seen);
    }
}
