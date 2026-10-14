using System.Net.Sockets;

namespace RSM.Safety.Redis;

/// <summary>Redis is unreachable or the connection broke. Wraps socket/IO errors so callers never depend on System.Net.Sockets.</summary>
public sealed class RedisUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The only Redis client in RSM: a tiny RESP2 client over a TCP socket that can send nothing but the
/// whitelisted read-only commands (<see cref="RedisCommandWhitelist"/>). One command at a time.
/// </summary>
public sealed class RespWhitelistClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>For tests and for the socket factory below.</summary>
    internal RespWhitelistClient(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    public static async Task<RespWhitelistClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = (int)timeout.TotalMilliseconds, SendTimeout = (int)timeout.TotalMilliseconds };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
            return new RespWhitelistClient(tcp.GetStream(), tcp);
        }
        catch (Exception ex) when (ex is SocketException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            tcp.Dispose();
            throw new RedisUnavailableException($"{host}:{port}: {ex.Message}", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Sends one whitelisted command and returns the reply. Rejected commands never touch the socket.</summary>
    public async Task<RespValue> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = RedisCommandWhitelist.Validate(args);
        var payload = Resp.Encode(command);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await Resp.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new RedisUnavailableException(ex.Message, ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<RespValue> ExecuteAsync(CancellationToken cancellationToken, params string[] args) => ExecuteAsync(args, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
        _lock.Dispose();
    }
}
