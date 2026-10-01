using Puck.Hosting;

namespace Puck.Shaders;

// Implicit package instances choose immutable fragment shapes before scheduling. A replacement uses the same node's
// ordinary candidate build, so its installed output remains available while new scratch and passes are prepared.
public sealed partial class RenderGraphRuntime {
    private readonly Dictionary<string, RenderGraphPackageFragment> m_packageFragments = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Package, RenderGraphPackageFragment Fragment), RenderGraphRuntimeGraph> m_fragmentGraphs = [];

    private void ForgetReplacedFragments(RenderGraphInstanceSet set, int[] kept) {
        foreach (var old in m_set.Instances) {
            if (set.IndexOf(name: old.Name) < 0) {
                m_packageFragments.Remove(key: old.Name);
            }
        }
        for (var index = 0; (index < kept.Length); index++) {
            if (kept[index] < 0) {
                m_packageFragments.Remove(key: set.Instances[index].Name);
            }
        }
    }
    private void RefreshPackageFragments() {
        for (var index = 0; (index < m_set.Instances.Count); index++) {
            var instance = m_set.Instances[index];

            if (!RunsPackage(instance: instance, packages: m_packages) ||
                !m_packages.TryGetFactory(package: instance.ExternalPackage!, factory: out var factory) ||
                !RenderGraphPackageCatalog.Engine.TryGet(id: instance.ExternalPackage!, package: out var declared)) {
                continue;
            }
            var fragment = (factory.FragmentOf(instance: instance.Name) ?? declared.Fragment!);

            if (m_packageFragments.TryGetValue(key: instance.Name, value: out var previous) && ReferenceEquals(objA: previous, objB: fragment)) {
                continue;
            }
            if ((previous is null) && ReferenceEquals(objA: fragment, objB: declared.Fragment)) {
                m_packageFragments[instance.Name] = fragment;
                continue;
            }
            var key = (instance.ExternalPackage!, fragment);

            if (!m_fragmentGraphs.TryGetValue(key: key, value: out var graph)) {
                graph = (PackageGraphOf(package: instance.ExternalPackage!, selected: fragment, fault: out var fault) ??
                    throw new InvalidDataException(message: $"Instance '{instance.Name}' selected a refused package fragment: {fault}"));
                m_fragmentGraphs.Add(key: key, value: graph);
            }
            m_nodes[index]!.Swap(pipeline: graph.Pipeline);
            m_graphs[index] = graph;
            m_packageFragments[instance.Name] = fragment;
        }
    }
}
