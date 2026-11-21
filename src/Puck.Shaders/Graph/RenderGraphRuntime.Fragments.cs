namespace Puck.Shaders;

// Implicit package instances choose immutable fragment shapes before scheduling. A replacement uses the same node's
// ordinary candidate build, so its installed output remains available while new scratch and passes are prepared.
public sealed partial class RenderGraphRuntime {
    private readonly PackageGraphs m_packageGraphs;

    // One graph per package and fragment, shared by every instance that selects it, and the fragment each package
    // instance renders. A kept instance whose fragment holds therefore keeps its pipeline across a reconfiguration and
    // only rebinds its inputs, where a pipeline made afresh would rebuild its targets.
    private sealed class PackageGraphs {
        private readonly Dictionary<(string Package, RenderGraphPackageFragment Fragment), RenderGraphRuntimeGraph> m_graphs = [];

        // The fragment each package instance's installed graph renders, by instance name.
        public Dictionary<string, RenderGraphPackageFragment> Fragments { get; } = new(comparer: StringComparer.Ordinal);

        // The fragment a package instance selects now: its factory's choice, or the package's declared fragment.
        public static RenderGraphPackageFragment? FragmentOf(string package, string instance, RenderGraphPackageRecorders packages) {
            packages.TryGetFactory(factory: out var factory, package: package);

            return (factory?.FragmentOf(instance: instance) ?? (RenderGraphPackageCatalog.Engine.TryGet(id: package, package: out var declared)
                ? declared.Fragment
                : null));
        }
        // The graph a package instance renders with the fragment it selects now, with the inputs its factory binds, or
        // null when the package runs as no fragment with an exported output.
        public RenderGraphRuntimeGraph? GraphFor(string package, string instance, RenderGraphPackageRecorders packages, out RenderGraphPackageFragment? fragment) {
            fragment = FragmentOf(instance: instance, package: package, packages: packages);

            if (fragment is null) {
                return null;
            }

            var key = (package, fragment);

            if (!m_graphs.TryGetValue(key: key, value: out var graph)) {
                graph = PackageGraphOf(package: package, selected: fragment, fault: out _);

                if (graph is null) {
                    return null;
                }

                m_graphs.Add(key: key, value: graph);
            }

            packages.TryGetFactory(factory: out var factory, package: package);

            return (graph with { Inputs = (factory?.InputsOf(instance: instance) ?? []) });
        }
        // Why an external instance of a package no producer or upload serves cannot run, or null when it runs as a
        // package instance, whose graph is then held for it.
        public string? RefusalOf(string package, string instance, RenderGraphPackageRecorders packages) {
            if (!packages.Serves(package: package)) {
                return "names a package neither an external producer, an upload nor a recorder serves";
            }

            var fragment = FragmentOf(instance: instance, package: package, packages: packages);

            if ((fragment is not null) && m_graphs.ContainsKey(key: (package, fragment))) {
                return null;
            }

            var graph = PackageGraphOf(fault: out var fault, package: package, selected: fragment);

            if ((graph is not null) && (fragment is not null)) {
                m_graphs.Add(key: (package, fragment), value: graph);
            }

            return fault;
        }
    }

    // Records the fragment of every package instance of the set the runtime now runs, from the graphs it was given, and
    // forgets the instances it no longer runs.
    private void RecordPackageFragments(RenderGraphPackageFragment?[] fragments) {
        m_packageGraphs.Fragments.Clear();

        for (var index = 0; (index < fragments.Length); index++) {
            if (fragments[index] is { } fragment) {
                m_packageGraphs.Fragments[m_set.Instances[index].Name] = fragment;
            }
        }
    }
    // The fragment changes this frame's refresh found, reused frame to frame.
    private readonly List<(int Index, RenderGraphPackageFragment Fragment, RenderGraphRuntimeGraph Graph)> m_fragmentChanges = [];
    // The changes a binding last refused as InputProducer and the set, graphs and producers it bound them against.
    // TryBindAll answers from those alone, so the same changes against the same three refuse again: they are tried once
    // one of them moves (a reconfiguration replaces the set, the graphs and the producers; a swap replaces the graphs).
    private readonly List<(int Index, RenderGraphPackageFragment Fragment, RenderGraphRuntimeGraph Graph)> m_refusedFragmentChanges = [];
    private object? m_refusedFragmentSet;
    private object? m_refusedFragmentGraphs;
    private object? m_refusedFragmentProducers;

    private void RefreshPackageFragments() {
        var changes = m_fragmentChanges;

        changes.Clear();
        for (var index = 0; (index < m_set.Instances.Count); index++) {
            var instance = m_set.Instances[index];

            if (!RunsPackage(instance: instance, packages: m_packages) ||
                (PackageGraphs.FragmentOf(instance: instance.Name, package: instance.ExternalPackage!, packages: m_packages) is not { } fragment)) {
                continue;
            }
            if (m_packageGraphs.Fragments.TryGetValue(key: instance.Name, value: out var previous) && ReferenceEquals(objA: previous, objB: fragment)) {
                continue;
            }

            var graph = (m_packageGraphs.GraphFor(fragment: out _, instance: instance.Name, package: instance.ExternalPackage!, packages: m_packages) ??
                throw new InvalidDataException(message: $"Instance '{instance.Name}' selected a refused package fragment: {m_packageGraphs.RefusalOf(instance: instance.Name, package: instance.ExternalPackage!, packages: m_packages)}"));

            if (ReferenceEquals(objA: graph.Pipeline, objB: m_graphs[index]?.Pipeline)) {
                m_packageGraphs.Fragments[instance.Name] = fragment;
                continue;
            }

            changes.Add(item: (index, fragment, graph));
        }
        if ((changes.Count == 0) || RefusedAlready(changes: changes)) {
            return;
        }

        var candidate = ((RenderGraphRuntimeGraph?[])m_graphs.Clone());

        foreach (var (index, _, graph) in changes) { candidate[index] = graph; }
        if (!TryBindAll(graphs: candidate, inputs: out var inputs, packages: m_packages, producers: m_producers, refusal: out var refusal, set: m_set)) {
            // A package resolves its instances' views as the frame begins, and a view that moved to another
            // residency (a portal window following its destination's scene) binds that residency's producers
            // before the host reconciles a set whose reads name them. The installed graphs stand for this frame,
            // and the changed fragments are tried again against the reconciled set.
            if (refusal.Code == RenderGraphRuntimeRefusalCode.InputProducer) {
                m_refusedFragmentChanges.Clear();
                m_refusedFragmentChanges.AddRange(collection: changes);
                m_refusedFragmentSet = m_set;
                m_refusedFragmentGraphs = m_graphs;
                m_refusedFragmentProducers = m_producers;
                return;
            }
            throw new InvalidDataException(message: refusal.Message);
        }
        m_refusedFragmentChanges.Clear();
        m_refusedFragmentSet = null;
        foreach (var (index, _, _) in changes) { m_nodes[index]!.RequireSwappable(pipeline: candidate[index]!.Pipeline); }
        foreach (var (index, fragment, _) in changes) {
            m_nodes[index]!.Swap(pipeline: candidate[index]!.Pipeline);
            m_packageGraphs.Fragments[m_set.Instances[index].Name] = fragment;
        }
        m_graphs = candidate;
        m_inputs = inputs;
    }
    // Whether these changes are the ones a binding last refused, against the set, graphs and producers it read.
    private bool RefusedAlready(List<(int Index, RenderGraphPackageFragment Fragment, RenderGraphRuntimeGraph Graph)> changes) {
        if (!ReferenceEquals(objA: m_refusedFragmentSet, objB: m_set) || !ReferenceEquals(objA: m_refusedFragmentGraphs, objB: m_graphs) ||
            !ReferenceEquals(objA: m_refusedFragmentProducers, objB: m_producers) || (m_refusedFragmentChanges.Count != changes.Count)) {
            return false;
        }
        for (var position = 0; (position < changes.Count); position++) {
            var (index, fragment, graph) = changes[position];
            var refused = m_refusedFragmentChanges[position];

            if ((refused.Index != index) || !ReferenceEquals(objA: refused.Fragment, objB: fragment) || !ReferenceEquals(objA: refused.Graph, objB: graph)) {
                return false;
            }
        }
        return true;
    }
}
