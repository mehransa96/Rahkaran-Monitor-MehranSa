using RSM.Core.Model;
using RSM.Core.Profiles;

namespace RSM.Core.Rules;

/// <summary>
/// A cross-node pass (e.g. IIS configuration drift between the App nodes of a farm): reads every node's facts and
/// returns extra facts per node id, which the rules then evaluate like any other fact. Pure: no I/O.
/// </summary>
public interface ISiteAnalyzer
{
    string Id { get; }

    /// <summary>Only results of these collectors change the analyzer's inputs (the live view re-runs it on them).</summary>
    IReadOnlySet<string> InputCollectors { get; }

    IReadOnlyDictionary<string, IReadOnlyList<Fact>> Analyze(SiteProfile site, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> factsByNode);
}
