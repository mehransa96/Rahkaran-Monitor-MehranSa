using System.Globalization;
using System.Text;

namespace RSM.Safety.Redis;

public enum RespKind
{
    SimpleString,
    Error,
    Integer,
    BulkString,
    Array,
    Null,
}

/// <summary>A parsed RESP2 value.</summary>
public sealed record RespValue(RespKind Kind, string? Text = null, long Integer = 0, IReadOnlyList<RespValue>? Items = null)
{
    public static readonly RespValue Nil = new(RespKind.Null);

    public bool IsError => Kind == RespKind.Error;

    public override string ToString() => Kind switch
    {
        RespKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        RespKind.Array => $"[{string.Join(", ", Items ?? [])}]",
        RespKind.Null => "(nil)",
        _ => Text ?? string.Empty,
    };
}

public sealed class RespProtocolException(string message) : Exception(message);

/// <summary>RESP2 encoder/decoder with size and depth limits (a misbehaving server cannot exhaust memory).</summary>
public static class Resp
{
    public const int MaxBulkBytes = 64 * 1024 * 1024;
    public const int MaxArrayItems = 200_000;
    public const int MaxDepth = 8;

    /// <summary>Encodes a command as a RESP array of bulk strings.</summary>
    public static byte[] Encode(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Count.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var arg in args)
        {
            var bytes = Encoding.UTF8.GetByteCount(arg);
            sb.Append('$').Append(bytes.ToString(CultureInfo.InvariantCulture)).Append("\r\n").Append(arg).Append("\r\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static async Task<RespValue> ReadAsync(Stream stream, CancellationToken cancellationToken, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new RespProtocolException("RESP nesting too deep");
        }

        var prefix = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        switch ((char)prefix)
        {
            case '+':
                return new RespValue(RespKind.SimpleString, line);
            case '-':
                return new RespValue(RespKind.Error, line);
            case ':':
                return new RespValue(RespKind.Integer, Integer: ParseLong(line));
            case '$':
            {
                var length = ParseLong(line);
                if (length < 0)
                {
                    return RespValue.Nil;
                }

                if (length > MaxBulkBytes)
                {
                    throw new RespProtocolException("bulk string too large");
                }

                var buffer = new byte[length + 2];
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (buffer[^2] != '\r' || buffer[^1] != '\n')
                {
                    throw new RespProtocolException("bulk string not terminated by CRLF");
                }

                return new RespValue(RespKind.BulkString, Encoding.UTF8.GetString(buffer, 0, (int)length));
            }

            case '*':
            {
                var count = ParseLong(line);
                if (count < 0)
                {
                    return RespValue.Nil;
                }

                if (count > MaxArrayItems)
                {
                    throw new RespProtocolException("array too large");
                }

                var items = new List<RespValue>((int)count);
                for (var i = 0; i < count; i++)
                {
                    items.Add(await ReadAsync(stream, cancellationToken, depth + 1).ConfigureAwait(false));
                }

                return new RespValue(RespKind.Array, Items: items);
            }

            default:
                throw new RespProtocolException($"unexpected RESP type byte 0x{prefix:x2}");
        }
    }

    private static long ParseLong(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new RespProtocolException($"invalid integer '{text}'");

    private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        await stream.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        return one[0];
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (b == '\r')
            {
                if (await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false) != '\n')
                {
                    throw new RespProtocolException("CR not followed by LF");
                }

                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            if (bytes.Count > 64 * 1024)
            {
                throw new RespProtocolException("line too long");
            }

            bytes.Add(b);
        }
    }
}

/// <summary>Parses the text returned by INFO into section → (key → value).</summary>
public static class RedisInfo
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string text)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = "default";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                if (current.Count > 0)
                {
                    sections[name] = current;
                }

                name = line.TrimStart('#', ' ');
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                current[line[..colon]] = line[(colon + 1)..];
            }
        }

        if (current.Count > 0)
        {
            sections[name] = current;
        }

        return sections;
    }
}
