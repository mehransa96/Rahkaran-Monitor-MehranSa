using System.Globalization;

namespace RSM.Safety.Redis;

public sealed class RedisCommandRejectedException(string message) : Exception(message);

/// <summary>
/// The complete set of Redis commands RSM may send (spec §2.3). Anything else — including write commands,
/// FLUSH*, CONFIG SET, DEBUG, CLIENT KILL, SAVE, EVAL, SHUTDOWN — is rejected before it reaches the socket.
/// </summary>
public static class RedisCommandWhitelist
{
    public static IReadOnlyList<string> AllowedSummary { get; } =
    [
        "AUTH", "PING", "INFO [section]", "DBSIZE", "SLOWLOG GET [n]", "SLOWLOG LEN", "LATENCY LATEST", "CLIENT LIST", "CONFIG GET maxmemory*",
    ];

    /// <summary>Validates a command and returns it normalised (command words upper-cased). Throws if not allowed.</summary>
    public static string[] Validate(IReadOnlyList<string> args)
    {
        if (args is null || args.Count == 0)
        {
            throw new RedisCommandRejectedException("empty command");
        }

        foreach (var arg in args)
        {
            if (arg is null || arg.Contains('\r') || arg.Contains('\n') || arg.Contains('\0'))
            {
                throw new RedisCommandRejectedException("control characters are not allowed in Redis arguments");
            }
        }

        var command = args[0].ToUpperInvariant();
        var sub = args.Count > 1 ? args[1].ToUpperInvariant() : null;
        var ok = command switch
        {
            "AUTH" => args.Count is 2 or 3,
            "PING" => args.Count == 1,
            "DBSIZE" => args.Count == 1,
            "INFO" => args.Count == 1 || (args.Count == 2 && args[1].All(c => char.IsAsciiLetterOrDigit(c) || c == '_')),
            "SLOWLOG" => (sub == "LEN" && args.Count == 2)
                || (sub == "GET" && (args.Count == 2 || (args.Count == 3 && IsCount(args[2])))),
            "LATENCY" => sub == "LATEST" && args.Count == 2,
            "CLIENT" => sub == "LIST" && args.Count == 2,
            "CONFIG" => sub == "GET" && args.Count == 3 && IsMaxMemoryPattern(args[2]),
            _ => false,
        };

        if (!ok)
        {
            throw new RedisCommandRejectedException($"Redis command '{Describe(args)}' is not on the read-only whitelist");
        }

        var normalised = args.ToArray();
        normalised[0] = command;
        if (sub is not null && command is "SLOWLOG" or "LATENCY" or "CLIENT" or "CONFIG")
        {
            normalised[1] = sub;
        }

        return normalised;
    }

    private static bool IsCount(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1000;

    private static bool IsMaxMemoryPattern(string value) =>
        value.StartsWith("maxmemory", StringComparison.OrdinalIgnoreCase)
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiLetterUpper(c) || c is '-' or '*');

    // Never echo AUTH arguments (passwords) into exception messages or logs.
    private static string Describe(IReadOnlyList<string> args) =>
        args[0].Equals("AUTH", StringComparison.OrdinalIgnoreCase) ? "AUTH ***" : string.Join(' ', args.Take(3));
}
