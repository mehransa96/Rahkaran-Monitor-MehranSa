using RSM.Core.Capabilities;
using RSM.Core.Profiles;
using RSM.Safety.Redis;

namespace RSM.Collectors.Redis;

/// <summary>Probes which whitelisted Redis commands work (renamed/disabled commands are tolerated).</summary>
public sealed class RedisCapabilityProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task ProbeAsync(RedisEndpoint endpoint, string? password, CapabilityMatrix matrix, CancellationToken cancellationToken)
    {
        RespWhitelistClient client;
        try
        {
            client = await RespWhitelistClient.ConnectAsync(endpoint.Host, endpoint.Port, Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (RedisUnavailableException ex)
        {
            SetAll(matrix, CapabilityState.Unreachable, ex.Message);
            return;
        }

        await using (client.ConfigureAwait(false))
        {
            try
            {
                if (!string.IsNullOrEmpty(password))
                {
                    var auth = await client.ExecuteAsync(cancellationToken, "AUTH", password).ConfigureAwait(false);
                    if (auth.IsError)
                    {
                        SetAll(matrix, CapabilityState.Denied, auth.Text);
                        return;
                    }
                }

                var ping = await client.ExecuteAsync(cancellationToken, "PING").ConfigureAwait(false);
                if (ping.IsError)
                {
                    // NOAUTH when a password is required but not configured.
                    SetAll(matrix, CapabilityState.Denied, ping.Text);
                    return;
                }

                matrix.Set(CapabilityIds.RedisPing, CapabilityState.Available);

                var info = await client.ExecuteAsync(cancellationToken, "INFO", "server").ConfigureAwait(false);
                matrix.Set(CapabilityIds.RedisInfo, Classify(info), info.IsError ? info.Text : null);
                if (!info.IsError && info.Text is { } text && RedisInfo.Parse(text).TryGetValue("Server", out var server) && server.TryGetValue("redis_version", out var version))
                {
                    matrix.Facts["redis.version"] = version;
                }

                var slowlog = await client.ExecuteAsync(cancellationToken, "SLOWLOG", "LEN").ConfigureAwait(false);
                matrix.Set(CapabilityIds.RedisSlowlog, Classify(slowlog), slowlog.IsError ? slowlog.Text : null);

                var clients = await client.ExecuteAsync(cancellationToken, "CLIENT", "LIST").ConfigureAwait(false);
                matrix.Set(CapabilityIds.RedisClientList, Classify(clients), clients.IsError ? clients.Text : null);
            }
            catch (Exception ex) when (ex is RedisUnavailableException or RespProtocolException)
            {
                SetAll(matrix, CapabilityState.Error, ex.Message);
            }
        }
    }

    private static CapabilityState Classify(RespValue reply) =>
        !reply.IsError ? CapabilityState.Available
        : reply.Text?.Contains("unknown command", StringComparison.OrdinalIgnoreCase) == true ? CapabilityState.NotInstalled
        : CapabilityState.Denied;

    private static void SetAll(CapabilityMatrix matrix, CapabilityState state, string? detail)
    {
        foreach (var id in new[] { CapabilityIds.RedisPing, CapabilityIds.RedisInfo, CapabilityIds.RedisSlowlog, CapabilityIds.RedisClientList })
        {
            matrix.Set(id, state, detail);
        }
    }
}
