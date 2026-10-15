using System.Globalization;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Sql;

/// <summary>One scheduler-monitor record row (catalog key <c>sql.cpu.ring_buffer</c>).</summary>
public sealed record RingBufferRow(string RecordXml, long RecordTicks, long NowTicks);

/// <summary>SQL CPU vs other processes at one minute (other = 100 − SQL − idle, never negative).</summary>
public sealed record CpuPoint(DateTimeOffset AtUtc, int SqlPercent, int IdlePercent, int OtherPercent);

/// <summary>Parses <c>RING_BUFFER_SCHEDULER_MONITOR</c> records: answers "is it SQL, or the app/IIS on the same box?".</summary>
public static class CpuRingBuffer
{
    public static IReadOnlyList<CpuPoint> Parse(IEnumerable<RingBufferRow> rows, DateTimeOffset nowUtc)
    {
        var points = new List<CpuPoint>();
        foreach (var row in rows)
        {
            var doc = SafeXml.TryParse(row.RecordXml, 64 * 1024);
            var health = doc?.Descendants("SystemHealth").FirstOrDefault();
            if (health is null
                || !int.TryParse(health.Element("ProcessUtilization")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sql)
                || !int.TryParse(health.Element("SystemIdle")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idle))
            {
                continue;
            }

            sql = Math.Clamp(sql, 0, 100);
            idle = Math.Clamp(idle, 0, 100);
            var at = nowUtc.AddMilliseconds(-(row.NowTicks - row.RecordTicks));
            points.Add(new CpuPoint(at, sql, idle, Math.Max(0, 100 - sql - idle)));
        }

        return points.OrderBy(p => p.AtUtc).ToList();
    }
}
