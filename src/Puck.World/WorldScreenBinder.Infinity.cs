using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

// Infinity layers use the same admitted mirrors, scene emitters, nested screens and residency retirement as session
// screens. A plan owns at most eight scenes; the cameras showing it add fitted outputs, never another scene or session.
internal sealed partial class WorldScreenBinder {
    private readonly Dictionary<(WorldSessionMirror Mirror, int Depth), InfinityRoot> m_infinityRoots = [];
    private readonly Dictionary<string, InfinityOutput> m_infinityOutputs = new(StringComparer.Ordinal);
    private int m_infinityEpoch;

    private InfinityRoot InfinityRootOf(WorldSessionMirror mirror, string instance, int depth, string name) {
        var key = (mirror, depth);
        if (!m_infinityRoots.TryGetValue(key, out var root)) {
            root = new InfinityRoot(mirror, instance, depth, name);
            m_infinityRoots.Add(key, root);
        }
        root.Epoch = m_infinityEpoch;
        FollowInfinity(root);
        return root;
    }

    private void ReconcileInfinityRoots() {
        m_infinityEpoch++;
        if (m_instanceHost.TryEndpoint(WorldInstanceHost.BootInstanceName, out var boot) && (boot is not null)) {
            _ = InfinityRootOf(boot.Mirror, WorldInstanceHost.BootInstanceName, 0, WorldViewGraphs.WorldInstance);
        }
        if (Presenter is { } presenter) {
            foreach (var scene in presenter.RoutedScenes) {
                if (presenter.Presents(scene)) {
                    _ = InfinityRootOf(scene.Endpoint.Mirror, scene.Endpoint.Identity, 0, RoutedViewName(scene));
                }
            }
        }
        EnsureFeeds();
        foreach (var feed in m_feeds) {
            _ = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);
        }
        foreach (var (key, root) in m_infinityRoots.ToArray()) {
            if (root.Epoch != m_infinityEpoch) {
                foreach (var entry in root.Entries.Values) { ReleaseInfinity(entry); }
                _ = m_infinityRoots.Remove(key);
                continue;
            }
            foreach (var (name, consumer) in root.Consumers.ToArray()) {
                var alive = consumer.Target switch {
                    WorldFramePresenter retainedPresenter => ReferenceEquals(retainedPresenter, Presenter),
                    WorldRoutedScene scene => Presenter?.Presents(scene) == true,
                    SessionFeed feed => m_feeds.Contains(feed),
                    InfinityEntry entry => entry.Root.Entries.Values.Contains(entry),
                    _ => false,
                };
                if (!alive) { _ = root.Consumers.Remove(name); }
            }
            PruneInfinityIndices(root);
        }
        RebuildInfinityOutputs();
    }

    private void FollowInfinity(InfinityRoot root) {
        var depth = Math.Max(0, BootNestingDepth() - root.Depth);
        if ((root.NestingDepth == depth)
            && root.Definitions.All(row => ReferenceEquals(row.Mirror.Definition, row.Definition))
            && root.Observations.All(row => ReferenceEquals(m_instanceHost.InfinitySession(row.Owner, row.Layer), row.Hosted)
                && ReferenceEquals(row.Hosted?.Observation, row.Observation)
                && (row.Available == (row.Hosted?.Observation is { Ended: false })))) {
            return;
        }

        root.Definitions.Clear();
        root.Observations.Clear();
        root.Definitions.Add((root.Mirror, root.Mirror.Definition));
        var owners = new Dictionary<string, (WorldSessionMirror Mirror, string Instance)>(StringComparer.Ordinal);
        var sources = new Dictionary<string, InfinitySource>(StringComparer.Ordinal);
        var roots = WorldInfinityViewSpecs.Of(root.Mirror.Definition.Render?.Sky);
        foreach (var spec in roots) { owners[WorldViewNames.Sky(spec.Name)] = (root.Mirror, root.Instance); }
        var plan = WorldInfinityViewPlan.Resolve(roots, Children, depth);

        foreach (var planned in plan.Views) {
            var source = Source(planned.Name, planned.Spec);
            var prototypes = ((planned.Spec.Kind == InfinityViewKind.Far)
                ? source.Mirror?.Definition.Render?.Sky?.Layers?.OfType<WorldRenderSkyLayer.Far>()
                    .FirstOrDefault(layer => layer.Name == planned.Spec.Name)?.Prototypes
                : null);
            var ready = (source.Mirror is not null) && ((source.Hosted is null) || (source.Hosted.Observation is { Ended: false }));
            if (root.Entries.TryGetValue(planned.Name, out var held)
                && (!ready || !ReferenceEquals(held.Mirror, source.Mirror) || !ReferenceEquals(held.Hosted, source.Hosted)
                    || (held.Planned.Spec != planned.Spec) || !held.Prototypes.SequenceEqual(prototypes ?? []))) {
                ReleaseInfinity(held);
                _ = root.Entries.Remove(planned.Name);
            }
            if (ready && !root.Entries.ContainsKey(planned.Name)) {
                var entry = CreateInfinity(root, planned, source.Mirror!, source.Instance!, source.Hosted, prototypes);
                root.Entries.Add(planned.Name, entry);
            }
        }
        foreach (var (name, entry) in root.Entries.ToArray()) {
            if (plan.Views.Any(view => view.Name == name)) { continue; }
            ReleaseInfinity(entry);
            _ = root.Entries.Remove(name);
        }

        root.Plan = plan;
        root.NestingDepth = depth;
        root.Subplans.Clear();
        foreach (var consumer in root.Consumers.Values) {
            consumer.Fit.Apply(root.PlanOf(consumer.Parent));
            consumer.Views.Begin();
            _ = consumer.Views.TryPublish(out _);
        }
        return;

        InfinitySource Source(string name, InfinityViewSpec spec) {
            if (sources.TryGetValue(name, out var cached)) { return cached; }
            if (!owners.TryGetValue(name, out var owner)) { return default; }
            if (spec.Kind == InfinityViewKind.Far) {
                cached = new InfinitySource(owner.Mirror, owner.Instance, null);
            } else {
                var hosted = m_instanceHost.InfinitySession(owner.Instance, spec.Name);
                root.Observations.Add(new InfinityObservation(owner.Instance, spec.Name, hosted,
                    hosted?.Observation, hosted?.Observation is { Ended: false }));
                cached = ((hosted is { InstanceName: { } instance, Observation: { Ended: false } })
                    ? new InfinitySource(hosted.Mirror, instance, hosted)
                    : default);
            }
            sources.Add(name, cached);
            return cached;
        }

        IReadOnlyList<InfinityViewSpec> Children(string name, InfinityViewSpec spec) {
            var source = Source(name, spec);
            if (source.Mirror is not { } mirror) { return []; }
            root.Definitions.Add((mirror, mirror.Definition));
            var children = WorldInfinityViewSpecs.Of(mirror.Definition.Render?.Sky);
            foreach (var child in children) { owners[WorldViewNames.NestedSky(name, child.Name)] = (mirror, source.Instance!); }
            return children;
        }
    }

    private InfinityEntry CreateInfinity(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror,
        string instance, WorldObservationSession? hosted, IReadOnlyList<string>? prototypes) {
        var name = ((root.Name == WorldViewGraphs.WorldInstance) ? planned.Name : (root.Name + Puck.State.GeneratedName.Joiner + planned.Name));
        var emitter = new WorldSessionSceneEmitter(mirror: mirror, effectiveCameraName: null, domains: m_domains,
            onlyPrototypes: ((planned.Spec.Kind == InfinityViewKind.Far) ? new HashSet<string>(prototypes ?? [], StringComparer.Ordinal) : null)) {
            SkyLayers = SkyLayers,
        };
        var entry = new InfinityEntry(root, planned, mirror, instance, hosted, name, emitter, prototypes ?? []);
        var scene = new WorldInfinityViewScene(emitter, () => entry.Views, planned.Spec) {
            FitSkyViews = (views, sky, width, height) => FilmInfinity(entry, views, sky, width, height),
        };
        var source = new SdfCompositionFrameSource(dresser: scene, emitters: [emitter]);
        entry.Source = source;
        if (planned.Spec.Kind == InfinityViewKind.World) {
            var screens = new WorldNestedScreens<SessionFeed>(definition: () => mirror.Definition,
                depth: root.Depth + planned.Depth, head: name, shares: SharesProducer, world: instance);
            entry.Screens = screens;
            m_nestedOwners.Add(screens, new NestedOwner(instance, () => mirror.Definition,
                () => ((entry.Views.Count == 0) ? null : entry.Views[0].Camera.Position), null, null) { Infinity = entry });
        }
        if (m_instanceHost.TryGet(instance, out var destination) && (destination is not null)) {
            entry.Envelope = destination.Server.Envelope.Configure(allowGrowth: true,
                programWordCapacity: source.WorstCaseProgramWordCapacity,
                instanceCapacity: source.WorstCaseInstanceCapacity,
                measure: candidate => ((hosted is null) ? emitter.MeasureCandidate(candidate)
                    : ((hosted.Observation?.Disclose(candidate) is { } disclosed) ? emitter.MeasureCandidate(disclosed) : (0, 0))));
        }
        m_feedsMoved = true;
        return entry;
    }

    private SdfWorldResidency? InfinityResidencyOf(InfinityEntry entry) {
        if (entry.Residency is { } held) { return held; }
        if ((m_viewPipelines is null) || (entry.Source is not { } source)) { return null; }
        var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            dynamicTransformCapacity: source.WorstCaseDynamicTransformCapacity,
            frameSource: new WorldSessionFrameSource(source, CaptureHostFirst),
            height: ((uint)m_viewDisplayHeight), width: ((uint)m_viewDisplayWidth),
            instanceCapacity: source.WorstCaseInstanceCapacity,
            programWordCapacity: source.WorstCaseProgramWordCapacity,
            kernels: ViewKernels(), name: entry.Name, pipelines: m_viewPipelines,
            screenSources: new InfinityScreenSources(this, entry));
        entry.Residency = residency;
        RegisterViewWork(lifetime: residency.WorkLifetime, name: entry.Name, transforms: source.MovedTransforms, work: residency.Work);
        return residency;
    }

    private bool TryResolveInfinityView(string name, out SdfWorldView view) {
        view = default;
        if (!m_infinityOutputs.TryGetValue(name, out var output) || (InfinityResidencyOf(output.Entry) is not { } residency)) { return false; }
        view = new SdfWorldView(residency, output.Index);
        return true;
    }

    private void ReleaseInfinity(InfinityEntry entry) {
        if (entry.Screens is { } screens) {
            screens.Close(NestedSessionsOf());
            _ = m_nestedOwners.Remove(screens);
        }
        entry.Envelope?.Dispose();
        entry.Emitter.Dispose();
        if (entry.Residency is { } residency) {
            UnregisterViewWork(entry.Name);
            residency.Dispose();
        }
        m_feedsMoved = true;
    }

    private void ReleaseInfinityRoots() {
        foreach (var root in m_infinityRoots.Values) {
            foreach (var entry in root.Entries.Values) { ReleaseInfinity(entry); }
        }
        m_infinityRoots.Clear();
        m_infinityOutputs.Clear();
    }

    private IEnumerable<InfinityEntry> InfinityEntries() => m_infinityRoots.Values.SelectMany(root => root.Entries.Values);

    private sealed class InfinityRoot(WorldSessionMirror mirror, string instance, int depth, string name) {
        public WorldSessionMirror Mirror { get; } = mirror;
        public string Instance { get; } = instance;
        public int Depth { get; } = depth;
        public string Name { get; } = name;
        public int Epoch { get; set; }
        public int NestingDepth { get; set; } = -1;
        public WorldInfinityViewPlan Plan { get; set; } = WorldInfinityViewPlan.Empty;
        public List<(WorldSessionMirror Mirror, WorldDefinition Definition)> Definitions { get; } = [];
        public List<InfinityObservation> Observations { get; } = [];
        public Dictionary<string, InfinityEntry> Entries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, InfinityConsumer> Consumers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, WorldInfinityViewPlan> Subplans { get; } = new(StringComparer.Ordinal);
        public WorldInfinityViewPlan PlanOf(string? parent) {
            if (parent is null) { return Plan; }
            if (!Subplans.TryGetValue(parent, out var plan)) { Subplans.Add(parent, plan = Plan.Below(parent)); }
            return plan;
        }
    }

    private sealed class InfinityEntry(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror, string instance,
        WorldObservationSession? hosted, string name, WorldSessionSceneEmitter emitter, IReadOnlyList<string> prototypes) {
        public InfinityRoot Root { get; } = root;
        public WorldInfinityView Planned { get; } = planned;
        public WorldSessionMirror Mirror { get; } = mirror;
        public string Instance { get; } = instance;
        public WorldObservationSession? Hosted { get; } = hosted;
        public string Name { get; } = name;
        public WorldSessionSceneEmitter Emitter { get; } = emitter;
        public IReadOnlyList<string> Prototypes { get; } = prototypes;
        public List<SdfViewSnapshot> Views { get; } = [];
        public Dictionary<string, int> Indices { get; } = new(StringComparer.Ordinal);
        public SdfCompositionFrameSource? Source { get; set; }
        public SdfWorldResidency? Residency { get; set; }
        public WorldNestedScreens<SessionFeed>? Screens { get; set; }
        public IDisposable? Envelope { get; set; }
        public QualityTier Tier { get; set; } = QualityTier.High;
    }

    private sealed class InfinityConsumer(string name, string? parent, object target, WorldInfinityViewPlan plan) {
        public WorldInfinityViews Fit { get; } = Create(name, plan);
        public WorldViewSet Views { get; } = new();
        public string? Parent { get; } = parent;
        public object Target { get; set; } = target;
        public bool Seen { get; set; }
        public SdfViewSnapshot Viewer { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public QualityTier Tier { get; set; }
        public GpuWorkSample Sample { get; } = new();
        private static WorldInfinityViews Create(string name, WorldInfinityViewPlan plan) {
            var fit = new WorldInfinityViews(name);
            fit.Apply(plan);
            return fit;
        }
    }

    private readonly record struct InfinitySource(WorldSessionMirror? Mirror, string? Instance, WorldObservationSession? Hosted);
    private readonly record struct InfinityObservation(string Owner, string Layer, WorldObservationSession? Hosted,
        WorldSessionObservation? Observation, bool Available);
    private readonly record struct InfinityOutput(InfinityEntry Entry, int Index);

    private sealed class InfinityScreenSources(WorldScreenBinder binder, InfinityEntry entry) : ISdfScreenSources {
        public IReadOnlyList<int> Screens => AllScreens;
        public Vector3 Light(int screen) => binder.LightOf(entry.Screens, screen);
        public SourceMapping? MappingOf(int screen) => (((entry.Screens is { } screens) && screens.Mappings.TryGet(screen, out var mapping)) ? mapping : null);
        public string? ReadOf(int view, int screen) => entry.Screens?.InstanceOf(screen);
    }
}
