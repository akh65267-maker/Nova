using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace Observability;

/// <summary>
/// Lets a service learn the real client address from the proxy in front of it. Shared by the gateway
/// (which hears it from whatever fronts the gateway) and by the services (which hear it from the gateway).
///
/// Without this, every request reaching UserService comes from the gateway, so anything keyed on
/// the caller's address (the login rate limit) is ONE bucket shared by everybody: a few failed
/// logins a minute from anyone block everyone. With it, the address is the one the gateway saw.
///
/// The header is believed only from the networks listed in "ForwardedHeaders:KnownNetworks" (CIDR
/// ranges, for example "172.16.0.0/12" for the Docker network), and only its last entry, which the
/// trusted proxy added itself, so a client cannot choose its own address by sending the header.
/// With nothing configured the header is ignored, which is the safe default.
/// </summary>
public static class TrustedForwardedHeaders
{
    /// <summary>Returns whether any network is trusted, that is, whether UseForwardedHeaders should run.</summary>
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        var networks = ParseNetworks(configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? []);
        if (networks.Count == 0)
            return false;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var network in networks)
                options.KnownNetworks.Add(network);
        });

        return true;
    }

    public static IReadOnlyList<IPNetwork> ParseNetworks(IEnumerable<string> cidrs)
    {
        var networks = new List<IPNetwork>();

        foreach (var cidr in cidrs.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var parts = cidr.Trim().Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks entry '{cidr}' is not a CIDR range such as 172.16.0.0/12.");

            networks.Add(new IPNetwork(address, prefix));
        }

        return networks;
    }
}
