using System.Globalization;

namespace RSM.Safety.Sql;

/// <summary>SQL Server product version (e.g. 16.0.4135). Catalog headers use "13", "13.0.5026" etc.</summary>
public readonly record struct SqlServerVersion(int Major, int Minor = 0, int Build = 0) : IComparable<SqlServerVersion>
{
    public static readonly SqlServerVersion Sql2016 = new(13);
    public static readonly SqlServerVersion Sql2016Sp2 = new(13, 0, 5026);
    public static readonly SqlServerVersion Sql2017 = new(14);
    public static readonly SqlServerVersion Sql2019 = new(15);
    public static readonly SqlServerVersion Sql2022 = new(16);

    public static SqlServerVersion Parse(string text)
    {
        if (!TryParse(text, out var v))
        {
            throw new FormatException($"Invalid SQL Server version '{text}'.");
        }

        return v;
    }

    public static bool TryParse(string? text, out SqlServerVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SqlServerVersion(numbers[0], numbers[1], numbers[2]);
        return version.Major > 0;
    }

    public int CompareTo(SqlServerVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Build.CompareTo(other.Build);

    public static bool operator <(SqlServerVersion a, SqlServerVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(SqlServerVersion a, SqlServerVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(SqlServerVersion a, SqlServerVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SqlServerVersion a, SqlServerVersion b) => a.CompareTo(b) >= 0;

    public string ProductName => Major switch
    {
        13 => "SQL Server 2016",
        14 => "SQL Server 2017",
        15 => "SQL Server 2019",
        16 => "SQL Server 2022",
        17 => "SQL Server 2025",
        < 13 => $"SQL Server (major {Major}, unsupported by RSM)",
        _ => $"SQL Server (major {Major})",
    };

    public override string ToString() => $"{Major}.{Minor}.{Build}";
}
