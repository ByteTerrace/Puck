using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    private readonly Dictionary<string, SdfWorldResidency> m_environmentResidencies = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, SdfWorldResidency?> m_environmentViews = new(comparer: StringComparer.Ordinal);

    /// <summary>The environment package sharing this host's existing residency and view resolution.</summary>
    public SdfSkyEnvironmentPasses? Environment { get; set; }

    private void ResetEnvironment() {
        foreach (var name in m_environmentResidencies.Keys) { Environment?.Unregister(name: name); }
        m_environmentResidencies.Clear();
        m_environmentViews.Clear();
    }
    private bool EnvironmentChanged() {
        if (Pickers is null || Environment is null) { return false; }
        foreach (var pair in m_environmentViews) {
            if (!ReferenceEquals(objA: pair.Value, objB: Pickers.ViewOf(instance: pair.Key)?.Residency)) { return true; }
        }
        return false;
    }
    private void AppendEnvironment(ref RenderGraphInstanceSet set, ref IReadOnlyList<RenderGraphRuntimeGraph?> graphs) {
        if (Pickers is null || Environment is null) { return; }
        var desired = new Dictionary<string, SdfWorldResidency>(comparer: StringComparer.Ordinal);
        var byView = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var sourceViews = new Dictionary<string, (int View, RenderGraphInstance Instance)>(comparer: StringComparer.Ordinal);

        m_environmentViews.Clear();
        foreach (var instance in set.Instances) {
            if (instance.ExternalPackage != RenderGraphPackageCatalog.SdfWorld) { continue; }
            var view = Pickers.ViewOf(instance: instance.Name);
            m_environmentViews.Add(key: instance.Name, value: view?.Residency);
            if (view is not { LightView: false } resolved) { continue; }
            var name = WorldViewNames.Environment(residency: resolved.Residency.Name);
            byView.Add(key: instance.Name, value: name);
            desired.TryAdd(key: name, value: resolved.Residency);
            if (!sourceViews.TryGetValue(key: name, value: out var previous) || resolved.View < previous.View) {
                sourceViews[name] = (resolved.View, instance);
            }
        }
        foreach (var pair in m_environmentResidencies) {
            if (!desired.TryGetValue(key: pair.Key, value: out var next) || !ReferenceEquals(objA: pair.Value, objB: next)) {
                Environment.Unregister(name: pair.Key);
            }
        }
        foreach (var pair in desired) { Environment.Register(name: pair.Key, residency: pair.Value, view: sourceViews[pair.Key].View); }
        m_environmentResidencies.Clear();
        foreach (var pair in desired) { m_environmentResidencies.Add(key: pair.Key, value: pair.Value); }

        var instances = set.Instances.Select(instance => byView.TryGetValue(key: instance.Name, value: out var environment)
            ? instance with { Reads = [.. instance.Reads, new(Producer: environment, Kind: ShaderPipelineResourceKind.Buffer)] }
            : instance).ToList();
        foreach (var pair in desired.OrderBy(static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            var source = sourceViews[pair.Key].Instance;
            var reads = SdfSkyEnvironmentGraph.ReadsOf(set: set, view: source.Name);
            instances.Add(item: new RenderGraphInstance(Name: pair.Key, Refresh: RenderGraphRefresh.EveryFrame, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count,
                Reads: reads, Output: ShaderPipelineResourceKind.Buffer, ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment) {
                OutputExtent = new RenderGraphPixelExtent(Width: SdfSkyEnvironment.Size, Height: SdfSkyEnvironment.Size),
            });
        }
        if (!RenderGraphInstanceSet.TryCreate(instances: instances, set: out var nextSet, refusal: out var refusal, nestingDepth: set.NestingDepth)) {
            throw new InvalidOperationException(message: refusal.Message);
        }
        graphs = [.. graphs, .. Enumerable.Repeat<RenderGraphRuntimeGraph?>(null, desired.Count)];
        set = nextSet;
    }
}
