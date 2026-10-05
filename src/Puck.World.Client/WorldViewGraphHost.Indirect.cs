using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    private readonly Dictionary<string, SdfWorldResidency> m_indirectResidencies = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, SdfWorldResidency?> m_indirectViews = new(comparer: StringComparer.Ordinal);

    private SdfIndirectTier m_lastIndirectTier;

    /// <summary>The indirect package recorder sharing this host's view resolution.</summary>
    public SdfIndirectPasses? Indirect { get; set; }
    /// <summary>The live session tier, read before the graph schedules any residency.</summary>
    public Func<SdfIndirectTier>? ReadIndirectTier { get; set; }
    /// <summary>Attaches or retires each residency's host work counters exactly once.</summary>
    public Action<SdfWorldResidency, bool>? IndirectResidencyChanged { get; set; }

    private void ResetIndirect() {
        foreach (var pair in m_indirectResidencies) {
            Indirect?.Unregister(name: pair.Key);
            Pickers?.UnregisterLightView(name: WorldViewNames.IndirectLight(cache: pair.Key));
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
            if (!ReferenceEquals(objA: pair.Value, objB: Pickers.ViewOf(instance: pair.Key)?.Residency)) { return true; }
        }
        return false;
    }
    private void AppendIndirect(ref RenderGraphInstanceSet set, ref IReadOnlyList<RenderGraphRuntimeGraph?> graphs) {
        if ((Pickers is null) || (Indirect is null)) { return; }
        var tier = (ReadIndirectTier?.Invoke() ?? SdfIndirectTier.Off);
        var desired = new Dictionary<string, SdfWorldResidency>(comparer: StringComparer.Ordinal);
        var cacheByView = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        m_indirectViews.Clear();
        foreach (var instance in set.Instances) {
            if (instance.ExternalPackage != RenderGraphPackageCatalog.SdfWorld) { continue; }
            var resolved = Pickers.ViewOf(instance: instance.Name)?.Residency;

            m_indirectViews.Add(key: instance.Name, value: resolved);
            if (resolved is not { } residency) { continue; }
            residency.IndirectTierOverride = tier;
            if (tier == SdfIndirectTier.Off) { continue; }
            var cache = residency.IndirectInstanceName;

            cacheByView.Add(key: instance.Name, value: cache);
            desired.TryAdd(key: cache, value: residency);
        }
        foreach (var pair in m_indirectResidencies) {
            if (desired.TryGetValue(key: pair.Key, value: out var next) && ReferenceEquals(objA: next, objB: pair.Value)) { continue; }
            Indirect.Unregister(name: pair.Key);
            Pickers.UnregisterLightView(name: WorldViewNames.IndirectLight(cache: pair.Key));
            IndirectResidencyChanged?.Invoke(pair.Value, false);
        }
        foreach (var pair in desired) {
            Indirect.Register(name: pair.Key, residency: pair.Value);
            Pickers.RegisterLightView(name: WorldViewNames.IndirectLight(cache: pair.Key), residency: pair.Value);
            if (!m_indirectResidencies.TryGetValue(key: pair.Key, value: out var previous) || !ReferenceEquals(objA: previous, objB: pair.Value)) {
                IndirectResidencyChanged?.Invoke(pair.Value, true);
            }
        }
        m_indirectResidencies.Clear();
        foreach (var pair in desired) { m_indirectResidencies.Add(key: pair.Key, value: pair.Value); }
        var before = set.Instances.Count;

        set = WorldIndirectGraph.Append(cacheByView: cacheByView, set: set);
        graphs = [.. graphs, .. Enumerable.Repeat<RenderGraphRuntimeGraph?>(null, (set.Instances.Count - before))];
        m_lastIndirectTier = tier;
    }
}
/// <summary>The buffer edges joining each view to its residency's cache and conservative light camera.</summary>
public static class WorldIndirectGraph {
    /// <summary>Adds a cache and depth-bank producer per residency, with both buffer edges for each enabled view.
    /// The light package supplies its native camera extent; these buffer producers declare no image extent.</summary>
    /// <param name="set">The composed views and image sources.</param>
    /// <param name="cacheByView">Enabled view names mapped to their residency's cache name.</param>
    /// <returns>The set with its cache producers and dependencies.</returns>
    /// <exception cref="InvalidOperationException">The requested edges do not form a valid instance set.</exception>
    public static RenderGraphInstanceSet Append(RenderGraphInstanceSet set, IReadOnlyDictionary<string, string> cacheByView) {
        if (cacheByView.Count == 0) { return set; }
        var instances = set.Instances.Select(selector: instance => (cacheByView.TryGetValue(key: instance.Name, value: out var cache)
            ? instance with { Reads = [.. instance.Reads, new(Producer: cache, Kind: ShaderPipelineResourceKind.Buffer), new(Producer: WorldViewNames.IndirectLight(cache: cache), Kind: ShaderPipelineResourceKind.Buffer)] }
            : instance)).ToList();

        foreach (var cache in cacheByView.Values.Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)) {
            var reads = new List<RenderGraphRead> {
                new(Producer: WorldViewNames.IndirectLight(cache: cache), Kind: ShaderPipelineResourceKind.Buffer),
            };
            foreach (var view in cacheByView.Where(pair => pair.Value == cache)) {
                foreach (var read in set.Instances[set.IndexOf(view.Key)].Reads) {
                    if (set.Instances[set.IndexOf(read.Producer)].ExternalPackage == RenderGraphPackageCatalog.SkyEnvironment &&
                        !reads.Any(existing => existing.Producer == read.Producer)) { reads.Add(read); }
                }
            }
            instances.Add(item: new(Name: WorldViewNames.IndirectLight(cache: cache), Refresh: RenderGraphRefresh.EveryFrame, Passes: SdfWorldPackage.LightViewFragment(maps: 0).Passes.Count, Reads: [],
                Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SdfWorld));
            var fragment = SdfWorldPackage.IndirectFragment(bytes: sizeof(uint));
            if (reads.Count > 1) { fragment = SdfSkyEnvironmentGraph.WithIndirectEnvironment(fragment); }
            instances.Add(item: new(Name: cache, Refresh: RenderGraphRefresh.EveryFrame, Passes: fragment.Passes.Count,
                Reads: reads,
                Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.Indirect));
        }
        if (!RenderGraphInstanceSet.TryCreate(instances, out var result, out var refusal, set.NestingDepth)) {
            throw new InvalidOperationException(message: refusal.Message);
        }
        return result;
    }
}
