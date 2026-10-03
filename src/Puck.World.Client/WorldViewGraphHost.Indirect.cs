using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    private readonly Dictionary<string, SdfWorldResidency> m_indirectResidencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SdfWorldResidency?> m_indirectViews = new(StringComparer.Ordinal);

    private SdfIndirectTier m_lastIndirectTier;

    /// <summary>The indirect package recorder sharing this host's view resolution.</summary>
    public SdfIndirectPasses? Indirect { get; set; }
    /// <summary>The live session tier, read before the graph schedules any residency.</summary>
    public Func<SdfIndirectTier>? ReadIndirectTier { get; set; }
    /// <summary>Attaches or retires each residency's host work counters exactly once.</summary>
    public Action<SdfWorldResidency, bool>? IndirectResidencyChanged { get; set; }

    private void ResetIndirect() {
        foreach (var pair in m_indirectResidencies) {
            Indirect?.Unregister(pair.Key);
            IndirectResidencyChanged?.Invoke(pair.Value, false);
        }
        m_indirectResidencies.Clear();
        m_indirectViews.Clear();
        m_lastIndirectTier = SdfIndirectTier.Off;
    }
    private bool IndirectChanged() {
        var tier = (ReadIndirectTier?.Invoke() ?? SdfIndirectTier.Off);

        if (tier != m_lastIndirectTier) { return true; }
        if ((tier == SdfIndirectTier.Off) || (Pickers is null)) { return false; }
        foreach (var pair in m_indirectViews) {
            if (!ReferenceEquals(pair.Value, Pickers.ViewOf(pair.Key)?.Residency)) { return true; }
        }
        return false;
    }
    private void AppendIndirect(ref RenderGraphInstanceSet set, ref IReadOnlyList<RenderGraphRuntimeGraph?> graphs) {
        if ((Pickers is null) || (Indirect is null)) { return; }
        var tier = (ReadIndirectTier?.Invoke() ?? SdfIndirectTier.Off);
        var desired = new Dictionary<string, SdfWorldResidency>(StringComparer.Ordinal);
        var cacheByView = new Dictionary<string, string>(StringComparer.Ordinal);

        m_indirectViews.Clear();
        foreach (var instance in set.Instances) {
            if (instance.ExternalPackage != RenderGraphPackageCatalog.SdfWorld) { continue; }
            var resolved = Pickers.ViewOf(instance.Name)?.Residency;

            m_indirectViews.Add(instance.Name, resolved);
            if (resolved is not { } residency) { continue; }
            residency.IndirectTierOverride = tier;
            if (tier == SdfIndirectTier.Off) { continue; }
            var cache = residency.IndirectInstanceName;

            cacheByView.Add(instance.Name, cache);
            desired.TryAdd(cache, residency);
        }
        foreach (var pair in m_indirectResidencies) {
            if (desired.TryGetValue(pair.Key, out var next) && ReferenceEquals(next, pair.Value)) { continue; }
            Indirect.Unregister(pair.Key);
            IndirectResidencyChanged?.Invoke(pair.Value, false);
        }
        foreach (var pair in desired) {
            Indirect.Register(pair.Key, pair.Value);
            if (!m_indirectResidencies.TryGetValue(pair.Key, out var previous) || !ReferenceEquals(previous, pair.Value)) {
                IndirectResidencyChanged?.Invoke(pair.Value, true);
            }
        }
        m_indirectResidencies.Clear();
        foreach (var pair in desired) { m_indirectResidencies.Add(pair.Key, pair.Value); }
        var before = set.Instances.Count;

        set = WorldIndirectGraph.Append(set, cacheByView);
        graphs = [.. graphs, .. Enumerable.Repeat<RenderGraphRuntimeGraph?>(null, (set.Instances.Count - before))];
        m_lastIndirectTier = tier;
    }
}
/// <summary>The buffer edges that join each view to its residency's one indirect-cache instance.</summary>
public static class WorldIndirectGraph {
    /// <summary>Adds one producer for each distinct cache and one buffer edge for each enabled view.</summary>
    /// <param name="set">The composed views and image sources.</param>
    /// <param name="cacheByView">Enabled view names mapped to their residency's cache name.</param>
    /// <returns>The set with its cache producers and dependencies.</returns>
    /// <exception cref="InvalidOperationException">The requested edges do not form a valid instance set.</exception>
    public static RenderGraphInstanceSet Append(RenderGraphInstanceSet set, IReadOnlyDictionary<string, string> cacheByView) {
        if (cacheByView.Count == 0) { return set; }
        var instances = set.Instances.Select(instance => (cacheByView.TryGetValue(instance.Name, out var cache)
            ? instance with { Reads = [.. instance.Reads, new(Producer: cache, Kind: ShaderPipelineResourceKind.Buffer)] }
            : instance)).ToList();

        foreach (var cache in cacheByView.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            instances.Add(new(Name: cache, Refresh: RenderGraphRefresh.EveryFrame, Passes: 3, Reads: [],
                Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.Indirect));
        }
        if (!RenderGraphInstanceSet.TryCreate(instances, out var result, out var refusal, set.NestingDepth)) {
            throw new InvalidOperationException(refusal.Message);
        }
        return result;
    }
}
