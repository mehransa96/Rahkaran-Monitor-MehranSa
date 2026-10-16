using System.Net;
using RSM.Core.Capabilities;
using RSM.Core.Model;
using RSM.Core.Profiles;
using RSM.Safety.Http;

namespace RSM.Collectors.Rahkaran;

/// <summary>Probes the RabbitMQ management API with one whitelisted GET (<c>/api/overview</c>).</summary>
public static class RabbitCapabilityProbe
{
    public static async Task ProbeAsync(RabbitEndpoint? endpoint, string? password, NodeRoles roles, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            matrix.Set(CapabilityIds.RabbitApi, (roles & NodeRoles.Workflow) != 0 ? CapabilityState.NotConfigured : CapabilityState.NotApplicable,
                "RabbitMQ management API is off by default; services are still checked");
            return;
        }

        try
        {
            using var transport = new RabbitNodeTransport(endpoint, password);
            var overview = RabbitMqCollector.ParseOverview(await transport.GetAsync("api/overview", cancellationToken).ConfigureAwait(false));
            matrix.Set(CapabilityIds.RabbitApi, CapabilityState.Available);
            if (overview.Version is { } version)
            {
                matrix.Facts["rmq.version"] = version;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            matrix.Set(CapabilityIds.RabbitApi, CapabilityState.Denied, $"HTTP {(int)ex.StatusCode!} — the user needs the 'monitoring' tag");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or HttpRequestRejectedException && !cancellationToken.IsCancellationRequested)
        {
            matrix.Set(CapabilityIds.RabbitApi, CapabilityState.Unreachable, ex.Message);
        }
    }
}
