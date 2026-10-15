using System.Globalization;
using System.Xml.Linq;
using RSM.Core.Model;
using RSM.Parsers.Xml;

namespace RSM.Parsers.Showplan;

public enum PlanWarningKind
{
    MissingIndex,
    ImplicitConversion,
    SpillToTempDb,
    KeyLookup,
    RidLookup,
    LargeScan,
    ExcessiveMemoryGrant,
    Parallelism,
    NoJoinPredicate,
}

public sealed record PlanWarning(PlanWarningKind Kind, string Detail);

public sealed record PlanAnalysis(
    IReadOnlyList<PlanWarning> Warnings,
    double? EstimatedCost,
    int? DegreeOfParallelism,
    long? DesiredMemoryKb,
    bool Parsed)
{
    public static readonly PlanAnalysis Unreadable = new([], null, null, null, false);

    public bool Has(PlanWarningKind kind) => Warnings.Any(w => w.Kind == kind);
}

/// <summary>Thresholds for the heuristic warnings (general guidance — configurable).</summary>
public sealed record ShowplanOptions(double LargeScanRows = 100_000, long ExcessiveGrantKb = 1024 * 1024);

/// <summary>
/// Reads cached showplan XML (no SSMS needed) and flags the warning types listed in spec §7.10. The plan is untrusted
/// input and goes through <see cref="SafeXml"/>; nothing is executed.
/// </summary>
public static class ShowplanAnalyzer
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly HashSet<string> ScanOps = new(StringComparer.OrdinalIgnoreCase) { "Table Scan", "Clustered Index Scan", "Index Scan" };

    public static PlanAnalysis Analyze(string? planXml, ShowplanOptions? options = null)
    {
        options ??= new ShowplanOptions();
        var doc = SafeXml.TryParse(planXml);
        if (doc?.Root is null || doc.Root.Name != Ns + "ShowPlanXML")
        {
            return PlanAnalysis.Unreadable;
        }

        var warnings = new List<PlanWarning>();
        void Add(PlanWarningKind kind, string detail)
        {
            if (!warnings.Any(w => w.Kind == kind && w.Detail == detail))
            {
                warnings.Add(new PlanWarning(kind, detail));
            }
        }

        foreach (var group in doc.Descendants(Ns + "MissingIndexGroup"))
        {
            var impact = Number(group.Attribute("Impact"));
            foreach (var index in group.Elements(Ns + "MissingIndex"))
            {
                var columns = index.Elements(Ns + "ColumnGroup")
                    .Select(cg => $"{(string?)cg.Attribute("Usage")}: {string.Join(", ", cg.Elements(Ns + "Column").Select(c => (string?)c.Attribute("Name")))}");
                Add(PlanWarningKind.MissingIndex,
                    string.Create(CultureInfo.InvariantCulture, $"{(string?)index.Attribute("Database")}.{(string?)index.Attribute("Schema")}.{(string?)index.Attribute("Table")} ({string.Join("; ", columns)}), impact {impact:0.#} %"));
            }
        }

        foreach (var convert in doc.Descendants(Ns + "PlanAffectingConvert"))
        {
            Add(PlanWarningKind.ImplicitConversion, $"{(string?)convert.Attribute("ConvertIssue")}: {(string?)convert.Attribute("Expression")}");
        }

        foreach (var scalar in doc.Descendants(Ns + "ScalarOperator"))
        {
            if (((string?)scalar.Attribute("ScalarString"))?.Contains("CONVERT_IMPLICIT", StringComparison.OrdinalIgnoreCase) == true
                && !warnings.Any(w => w.Kind == PlanWarningKind.ImplicitConversion))
            {
                Add(PlanWarningKind.ImplicitConversion, Truncate((string?)scalar.Attribute("ScalarString")));
            }
        }

        foreach (var spill in doc.Descendants().Where(e => e.Name == Ns + "SpillToTempDb" || e.Name == Ns + "SortSpillDetails" || e.Name == Ns + "HashSpillDetails"))
        {
            var op = spill.Ancestors(Ns + "RelOp").FirstOrDefault();
            Add(PlanWarningKind.SpillToTempDb, $"{(string?)op?.Attribute("PhysicalOp") ?? spill.Name.LocalName}, spill level {(string?)spill.Attribute("SpillLevel") ?? "?"}");
        }

        if (doc.Descendants(Ns + "Warnings").Any(w => IsTrue(w.Attribute("NoJoinPredicate"))) || doc.Descendants(Ns + "NoJoinPredicate").Any())
        {
            Add(PlanWarningKind.NoJoinPredicate, "a join has no join predicate (possible Cartesian product)");
        }

        foreach (var relOp in doc.Descendants(Ns + "RelOp"))
        {
            var physical = (string?)relOp.Attribute("PhysicalOp") ?? string.Empty;
            var table = ObjectName(relOp);
            if (physical.Equals("Key Lookup", StringComparison.OrdinalIgnoreCase)
                || (physical.Equals("Clustered Index Seek", StringComparison.OrdinalIgnoreCase) && relOp.Elements(Ns + "IndexScan").Any(i => IsTrue(i.Attribute("Lookup")))))
            {
                Add(PlanWarningKind.KeyLookup, table);
            }
            else if (physical.Equals("RID Lookup", StringComparison.OrdinalIgnoreCase))
            {
                Add(PlanWarningKind.RidLookup, table);
            }

            if (ScanOps.Contains(physical))
            {
                var rows = new[] { Number(relOp.Attribute("EstimatedRowsRead")), Number(relOp.Attribute("TableCardinality")), Number(relOp.Attribute("EstimateRows")) }.Max();
                if (rows >= options.LargeScanRows)
                {
                    Add(PlanWarningKind.LargeScan, string.Create(CultureInfo.InvariantCulture, $"{physical} on {table} (~{rows:0} rows)"));
                }
            }
        }

        var queryPlans = doc.Descendants(Ns + "QueryPlan").ToList();
        var dop = queryPlans.Select(q => (int?)Number(q.Attribute("DegreeOfParallelism"))).Max();
        if (dop > 1 || doc.Descendants(Ns + "RelOp").Any(r => IsTrue(r.Attribute("Parallel")) || "Parallelism".Equals((string?)r.Attribute("PhysicalOp"), StringComparison.OrdinalIgnoreCase)))
        {
            Add(PlanWarningKind.Parallelism, dop > 1 ? $"DOP {dop}" : "parallel operators");
        }

        long? desired = null;
        foreach (var grant in doc.Descendants(Ns + "MemoryGrantInfo"))
        {
            var want = (long)Number(grant.Attribute("SerialDesiredMemory"));
            var granted = (long)Number(grant.Attribute("GrantedMemory"));
            var used = (long)Number(grant.Attribute("MaxUsedMemory"));
            desired = Math.Max(desired ?? 0, want);
            if (want >= options.ExcessiveGrantKb || (granted >= 100 * 1024 && used > 0 && granted > 10 * used))
            {
                Add(PlanWarningKind.ExcessiveMemoryGrant, string.Create(CultureInfo.InvariantCulture, $"desired {want / 1024} MB, granted {granted / 1024} MB, used {used / 1024} MB"));
            }
        }

        foreach (var w in doc.Descendants(Ns + "MemoryGrantWarning"))
        {
            Add(PlanWarningKind.ExcessiveMemoryGrant, $"{(string?)w.Attribute("GrantWarningKind")}: requested {(string?)w.Attribute("RequestedMemory")} KB, used {(string?)w.Attribute("MaxUsedMemory")} KB");
        }

        var cost = doc.Descendants(Ns + "StmtSimple").Select(s => Number(s.Attribute("StatementSubTreeCost"))).DefaultIfEmpty(0).Max();
        return new PlanAnalysis(warnings, cost > 0 ? cost : null, dop, desired, true);
    }

    public static LocalizedText Explain(PlanWarningKind kind) => kind switch
    {
        PlanWarningKind.MissingIndex => new("SQL Server ایندکسی پیشنهاد کرده است؛ ساختار راهکاران متعلق به سازنده است — بدون ستاد فنی ایندکس نسازید.",
            "SQL Server suggests an index; the Rahkaran schema is vendor-owned — do not create indexes without ستاد فنی."),
        PlanWarningKind.ImplicitConversion => new("تبدیل نوع ضمنی (CONVERT_IMPLICIT) ممکن است مانع استفاده از ایندکس شود.",
            "An implicit conversion (CONVERT_IMPLICIT) can prevent index seeks."),
        PlanWarningKind.SpillToTempDb => new("عملگر به tempdb سرریز کرده است (حافظهٔ ناکافی برای Sort/Hash).",
            "An operator spilled to tempdb (not enough memory for a sort/hash)."),
        PlanWarningKind.KeyLookup => new("Key Lookup: برای هر سطر یک جست‌وجوی اضافه در ایندکس Clustered.",
            "Key Lookup: one extra clustered-index seek per row."),
        PlanWarningKind.RidLookup => new("RID Lookup روی جدول بدون ایندکس Clustered.", "RID Lookup on a heap."),
        PlanWarningKind.LargeScan => new("پیمایش کامل یک جدول یا ایندکس بزرگ.", "Full scan of a large table or index."),
        PlanWarningKind.ExcessiveMemoryGrant => new("درخواست حافظهٔ بزرگ یا بیش از نیاز؛ می‌تواند دیگران را منتظر RESOURCE_SEMAPHORE کند.",
            "Large or excessive memory grant; can make others wait on RESOURCE_SEMAPHORE."),
        PlanWarningKind.Parallelism => new("پرس‌وجو موازی اجرا می‌شود؛ با MAXDOP و cost threshold مقایسه کنید.",
            "The query runs in parallel; compare with MAXDOP and cost threshold."),
        _ => new("Join بدون شرط اتصال (احتمال ضرب دکارتی).", "Join without a join predicate (possible Cartesian product)."),
    };

    private static string ObjectName(XElement relOp)
    {
        var obj = relOp.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault();
        return obj is null ? "?" : string.Join('.', new[] { (string?)obj.Attribute("Schema"), (string?)obj.Attribute("Table"), (string?)obj.Attribute("Index") }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static bool IsTrue(XAttribute? attribute) => (string?)attribute is "1" or "true" or "True";

    private static double Number(XAttribute? attribute) =>
        double.TryParse((string?)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string Truncate(string? text) => text is null ? string.Empty : text.Length > 300 ? text[..300] + "…" : text;
}
