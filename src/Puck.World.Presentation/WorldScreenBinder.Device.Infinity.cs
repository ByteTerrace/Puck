using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;

namespace Puck.World;

// Infinity layers use the same admitted mirrors, scene emitters, nested screens and residency retirement as session
// screens. A plan owns at most eight scenes; the cameras showing it add fitted outputs, never another scene or session.
public sealed partial class WorldScreenBinder {
    private readonly Dictionary<(WorldSessionMirror Mirror, int Depth), InfinityRoot> m_infinityRoots = [];
    private Dictionary<string, InfinityOutput> m_infinityOutputs = new(comparer: StringComparer.Ordinal);
    private Dictionary<string, InfinityOutput> m_infinityOutputsPrev = new(comparer: StringComparer.Ordinal);
    // Scratch for the roots and consumers one reconcile retires; the binder runs on the presentation thread alone.
    private readonly List<(WorldSessionMirror Mirror, int Depth)> m_staleInfinityRoots = [];
    private readonly List<string> m_deadInfinityConsumers = [];

    private int m_infinityEpoch;

    // A routed scene's root is named by the scene, a name spelled only when the root is made.
    private InfinityRoot InfinityRootOf(WorldSessionMirror mirror, string instance, int depth, string? name, WorldRoutedScene? routed = null) {
        var key = (mirror, depth);

        if (!m_infinityRoots.TryGetValue(key: key, value: out var root)) {
            root = new InfinityRoot(depth: depth, instance: instance, mirror: mirror, name: (name ?? RoutedViewName(scene: routed!)));
            m_infinityRoots.Add(key: key, value: root);
        }
        root.Epoch = m_infinityEpoch;
        FollowInfinity(root: root);
        return root;
    }
    private void ReconcileInfinityRoots() {
        m_infinityEpoch++;
        PublishFarProducts();
        if (Observations.BootEndpoint is { } boot) {
            _ = InfinityRootOf(boot.Mirror, WorldDefinitionLoader.BootInstanceName, 0, WorldViewGraphs.WorldInstance);
        }
        if (Presenter is { } presenter) {
            foreach (var scene in presenter.RoutedScenes) {
                if (presenter.Presents(scene: scene)) {
                    _ = InfinityRootOf(scene.Mirror, scene.Authority, 0, name: null, routed: scene);
                }
            }
        }
        EnsureFeeds();
        foreach (var feed in m_feeds) {
            _ = InfinityRootOf(feed.Mirror, feed.InstanceName, feed.Depth, feed.RegistrationName);
        }
        m_staleInfinityRoots.Clear();
        foreach (var (key, root) in m_infinityRoots) {
            if (root.Epoch != m_infinityEpoch) { m_staleInfinityRoots.Add(item: key); }
        }
        foreach (var key in m_staleInfinityRoots) {
            var root = m_infinityRoots[key];

            foreach (var entry in root.Entries.Values) { LeaveInfinity(entry: entry, root: root); }
            m_renderProbe?.UnregisterInfinityPlan(owner: root);
            _ = m_infinityRoots.Remove(key: key);
        }
        foreach (var root in m_infinityRoots.Values) {
            m_deadInfinityConsumers.Clear();
            foreach (var (name, consumer) in root.Consumers) {
                var alive = consumer.Target switch {
                    WorldFramePresenter retainedPresenter => ReferenceEquals(objA: retainedPresenter, objB: Presenter),
                    WorldRoutedScene scene => (Presenter?.Presents(scene: scene) == true),
                    SessionFeed feed => m_feeds.Contains(item: feed),
                    InfinityEntry entry => (entry.Roots.Count != 0),
                    // The boot frame's home films, which FitBootSky retires by its own begin and end each frame.
                    _ when ReferenceEquals(objA: consumer.Target, objB: m_homeFilms) => (Presenter is not null),
                    _ => false,
                };

                if (!alive || (System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: consumer.FittedAt).TotalSeconds >= InfinityConsumerRetireSeconds)) {
                    m_deadInfinityConsumers.Add(item: name);
                }
            }
            foreach (var name in m_deadInfinityConsumers) { _ = root.Consumers.Remove(key: name); }
            PruneInfinityIndices(root: root);
        }
        RebuildInfinityOutputs();
        ReleaseUnreadInfinityResidencies();
    }

    /// <summary>The seconds a consumer of a sky's infinity layers stands after its target last fitted them: a session,
    /// routed world or layer that has not rendered its sky for this long no longer reads them, as long as a parked
    /// residency keeps its lighting (<see cref="SdfWorldResidency.ForgetLightingSeconds"/>).</summary>
    public const double InfinityConsumerRetireSeconds = SdfWorldResidency.ForgetLightingSeconds;

    // Scratch for the entries the outputs name; the binder runs on the presentation thread alone.
    private readonly HashSet<InfinityEntry> m_namedInfinityEntries = new(comparer: ReferenceEqualityComparer.Instance);

    // Releases the residency of every layer no output names: one is made the first time its view is resolved
    // (InfinityResidencyOf), and a layer whose consumers all retired has no view left to render, so its residency would
    // otherwise stand for as long as the world whose sky lists it, one for every session that ever showed that sky. The
    // entry stays planned, and makes a residency again when a consumer fits it and its view is next resolved.
    private void ReleaseUnreadInfinityResidencies() {
        m_namedInfinityEntries.Clear();
        foreach (var output in m_infinityOutputs.Values) { _ = m_namedInfinityEntries.Add(item: output.Entry); }
        foreach (var root in m_infinityRoots.Values) {
            foreach (var entry in root.Entries.Values) {
                if ((entry.Residency is not { } residency) || m_namedInfinityEntries.Contains(item: entry)) { continue; }
                entry.Residency = null;
                UnregisterViewWork(name: entry.Name);
                residency.Dispose();
                m_feedsMoved = true;
            }
        }
    }
    // Called for every root several times a frame, so the check that nothing it planned from moved runs apart from
    // the replan, whose closures would otherwise be allocated on every call.
    private void FollowInfinity(InfinityRoot root) {
        var depth = Math.Max(val1: 0, val2: (BootNestingDepth() - root.Depth));

        if ((root.NestingDepth != depth) || InfinityInputsMoved(root: root)) {
            ReplanInfinity(depth: depth, root: root);
        }
    }
    private bool InfinityInputsMoved(InfinityRoot root) {
        if ((root.FarVersion != m_farVersion) && root.Plan.Views.Any(predicate: static view => view.Spec.FarProduct)) { return true; }
        foreach (var row in root.Skies) {
            if (!ReferenceEquals(objA: row.Mirror.Definition.Render?.Sky, objB: row.Sky)) { return true; }
        }
        foreach (var row in root.Observations) {
            var hosted = Observations.Infinity(layer: row.Layer, owner: row.Owner);

            if (!ReferenceEquals(objA: hosted?.Token, objB: row.Hosted?.Token)
                || !ReferenceEquals(objA: hosted?.Observation?.Token, objB: row.Observation?.Token)
                || (row.Available != (hosted?.Observation is { Ended: false }))) {
                return true;
            }
        }
        return false;
    }
    private void ReplanInfinity(InfinityRoot root, int depth) {
        root.Skies.Clear();
        root.Observations.Clear();
        root.Skies.Add(item: (root.Mirror, root.Mirror.Definition.Render?.Sky));
        var owners = new Dictionary<string, (WorldSessionMirror Mirror, string Instance)>(comparer: StringComparer.Ordinal);
        var sources = new Dictionary<string, InfinitySource>(comparer: StringComparer.Ordinal);
        var roots = WorldInfinityViewSpecs.Of(root.Mirror.Definition.Render?.Sky);

        foreach (var spec in roots) { owners[WorldViewNames.Sky(layer: spec.Name)] = (root.Mirror, root.Instance); }
        var plan = WorldInfinityViewPlan.Resolve(roots, Children, depth);

        foreach (var planned in plan.Views) {
            var source = Source(name: planned.Name, spec: planned.Spec);
            var prototypes = ((planned.Spec.Kind == InfinityViewKind.Far)
                ? source.Mirror?.Definition.Render?.Sky?.Layers?.OfType<WorldRenderSkyLayer.Far>()
                    .FirstOrDefault(predicate: layer => (layer.Name == planned.Spec.Name))?.Prototypes
                : null);
            var ready = ((source.Mirror is not null) && ((source.Hosted is null) || (source.Hosted.Observation is { Ended: false })));
            // A view asking for its world's far product draws the product once it is built. Until then it draws the world
            // whole, which is the product's source, unless the presentation holds a residency of that world anyway, whose
            // product is on its way: then it draws its fallback for the moments the build takes.
            var far = ((ready && planned.Spec.FarProduct) ? FarProductOf(instance: source.Instance!, mirror: source.Mirror!) : null);
            var waits = (ready && planned.Spec.FarProduct && (far is null) && FarSourceResident(instance: source.Instance!, mirror: source.Mirror!));

            if (root.Entries.TryGetValue(key: planned.Name, value: out var held)
                && (!ready || waits || !ReferenceEquals(objA: held.Mirror, objB: source.Mirror) || !ReferenceEquals(objA: held.Hosted?.Token, objB: source.Hosted?.Token)
                    || (held.Planned.Spec != planned.Spec) || !held.Prototypes.SequenceEqual(second: (prototypes ?? []))
                    || !ReferenceEquals(objA: held.Far, objB: far))) {
                _ = root.Entries.Remove(key: planned.Name);
                LeaveInfinity(entry: held, root: root);
            }
            if (ready && !waits && !root.Entries.ContainsKey(key: planned.Name)) {
                root.Entries.Add(key: planned.Name, value: JoinInfinity(root, planned, source.Mirror!, source.Instance!, source.Hosted, prototypes, far));
            }
        }
        foreach (var (name, entry) in root.Entries.ToArray()) {
            if (plan.Views.Any(predicate: view => (view.Name == name))) { continue; }
            _ = root.Entries.Remove(key: name);
            LeaveInfinity(entry: entry, root: root);
        }

        root.Plan = plan;
        root.FarVersion = m_farVersion;
        m_renderProbe?.RegisterInfinityPlan(root, root.Name, plan);
        root.NestingDepth = depth;
        root.Subplans.Clear();
        foreach (var consumer in root.Consumers.Values) {
            consumer.Fit.Apply(plan: root.PlanOf(parent: consumer.Parent));
            consumer.Views.Begin();
            _ = consumer.Views.TryPublish(instances: out _);
        }
        return;

        InfinitySource Source(string name, InfinityViewSpec spec) {
            if (sources.TryGetValue(key: name, value: out var cached)) { return cached; }
            if (!owners.TryGetValue(key: name, value: out var owner)) { return default; }
            if (spec.Kind == InfinityViewKind.Far) {
                cached = new InfinitySource(Hosted: null, Instance: owner.Instance, Mirror: owner.Mirror);
            } else {
                var hosted = Observations.Infinity(layer: spec.Name, owner: owner.Instance);

                root.Observations.Add(item: new InfinityObservation(owner.Instance, spec.Name, hosted,
                    hosted?.Observation, (hosted?.Observation is { Ended: false })));
                cached = ((hosted is { InstanceName: { } instance, Observation: { Ended: false } })
                    ? new InfinitySource(hosted.Mirror, instance, hosted)
                    : default);
            }
            sources.Add(key: name, value: cached);
            return cached;
        }

        IReadOnlyList<InfinityViewSpec> Children(string name, InfinityViewSpec spec) {
            var source = Source(name: name, spec: spec);

            if (source.Mirror is not { } mirror) { return []; }
            root.Skies.Add(item: (mirror, mirror.Definition.Render?.Sky));
            var children = WorldInfinityViewSpecs.Of(mirror.Definition.Render?.Sky);

            foreach (var child in children) { owners[WorldViewNames.NestedSky(name, child.Name)] = (mirror, source.Instance!); }
            return children;
        }
    }
    // Every layer of one content, whichever roots plan it: a layer is a function of the world it shows (the mirror and
    // the hosted session it is drawn from), its spec, the far product it draws when it draws one, the prototypes a far
    // layer keeps and, where the world holds screens,
    // the nesting depth they open at. The roots planning one content hold one entry, so one residency, whose views are
    // every holder's consumers'. JoinInfinity and LeaveInfinity are the only doors an entry enters or leaves a root by.
    private readonly Dictionary<InfinityContent, InfinityEntry> m_infinityContents = [];

    private static InfinityContent ContentOf(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror,
        WorldObservedSession? hosted, IReadOnlyList<string>? prototypes, SdfFarProduct? far) => new(
            Far: far,
            Mirror: mirror,
            Hosted: hosted?.Token,
            Spec: planned.Spec,
            Prototypes: string.Join(separator: '\n', values: (prototypes ?? [])),
            ScreenDepth: (((planned.Spec.Kind == InfinityViewKind.World) && (mirror.Definition.Screens.Count != 0)) ? (root.Depth + planned.Depth) : -1));
    // The entry of a planned layer's content, made by the first root that plans it and joined by every other.
    private InfinityEntry JoinInfinity(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror,
        string instance, WorldObservedSession? hosted, IReadOnlyList<string>? prototypes, SdfFarProduct? far) {
        var content = ContentOf(far: far, hosted: hosted, mirror: mirror, planned: planned, prototypes: prototypes, root: root);

        if (!m_infinityContents.TryGetValue(key: content, value: out var entry)) {
            entry = ((far is not null)
                ? CreateFarInfinity(root, planned, mirror, instance, hosted, far)
                : CreateInfinity(root, planned, mirror, instance, hosted, prototypes));
            entry.Identity = content;
            m_infinityContents.Add(key: content, value: entry);
        }
        entry.Roots.Add(item: root);
        m_infinityEntriesMoved = true;
        return entry;
    }
    // Takes a root's hold on an entry away, its consumers' views with it, and releases the entry once no root holds it.
    private void LeaveInfinity(InfinityEntry entry, InfinityRoot root) {
        _ = entry.Roots.Remove(item: root);
        m_infinityEntriesMoved = true;
        if (entry.Roots.Count != 0) {
            PruneInfinityIndices(entry: entry);
            return;
        }
        _ = m_infinityContents.Remove(key: entry.Identity);
        ReleaseInfinity(entry: entry);
    }
    private InfinityEntry CreateInfinity(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror,
        string instance, WorldObservedSession? hosted, IReadOnlyList<string>? prototypes) {
        var name = ((root.Name == WorldViewGraphs.WorldInstance) ? planned.Name : ((root.Name + Puck.State.GeneratedName.Joiner) + planned.Name));
        var emitter = new WorldSessionSceneEmitter(mirror: mirror, effectiveCameraName: null, domains: m_domains, jobs: m_stamps.Jobs,
            onlyPrototypes: ((planned.Spec.Kind == InfinityViewKind.Far) ? new HashSet<string>(collection: (prototypes ?? []), comparer: StringComparer.Ordinal) : null)) {
            SkyLayers = SkyLayers,
            Session = Settings,
        };
        var entry = new InfinityEntry(current: Current, emitter: emitter, hosted: hosted, instance: instance, mirror: mirror, name: name, planned: planned, prototypes: (prototypes ?? []), root: root);
        var scene = new WorldInfinityViewScene(emitter, () => entry.Views, planned.Spec) {
            FitSkyViews = (views, sky, width, height) => FilmInfinity(entry: entry, height: height, sky: sky, views: views, width: width),
        };
        var source = new SdfCompositionFrameSource(dresser: scene, emitters: [emitter]);

        entry.Source = source;
        if (planned.Spec.Kind == InfinityViewKind.World) {
            var screens = new WorldNestedScreens<SessionFeed>(definition: () => mirror.Definition,
                depth: (root.Depth + planned.Depth), head: name, shares: SharesProducer, world: instance);

            entry.Screens = screens;
            m_nestedOwners.Add(key: screens, value: new NestedOwner(instance, () => mirror.Definition,
                () => ((entry.Views.Count == 0) ? null : entry.Views[0].Camera), null, null) { Infinity = entry });
            m_accessBindingsVersion++;
        }
        if (Observations.EnvelopeOf(instance: instance) is { } envelope) {
            entry.Envelope = envelope.Configure(
                measure: candidate => ((entry.Hosted is not { } session) ? emitter.MeasureCandidate(candidate: candidate)
                    : ((session.Observation?.Disclose(arg: candidate) is { } disclosed) ? emitter.MeasureCandidate(candidate: disclosed) : default)));
        }
        m_feedsMoved = true;
        m_infinityEntriesMoved = true;
        return entry;
    }
    // A far view's entry: the product's emitter is its whole scene, and the world's own session emitter only dresses its
    // frames with that world's sky, lights and exposure, holding none of the world (no prototype, so no placement, body,
    // stamp or screen). It films no camera screen and measures no envelope, since its scene is the product alone.
    private InfinityEntry CreateFarInfinity(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror,
        string instance, WorldObservedSession? hosted, SdfFarProduct product) {
        var name = ((root.Name == WorldViewGraphs.WorldInstance) ? planned.Name : ((root.Name + Puck.State.GeneratedName.Joiner) + planned.Name));
        var dresser = new WorldSessionSceneEmitter(mirror: mirror, effectiveCameraName: null, domains: m_domains, jobs: m_stamps.Jobs,
            onlyPrototypes: new HashSet<string>(comparer: StringComparer.Ordinal)) {
            SkyLayers = SkyLayers,
            Session = Settings,
        };
        var entry = new InfinityEntry(current: Current, emitter: dresser, hosted: hosted, instance: instance, mirror: mirror, name: name, planned: planned, prototypes: [], root: root) {
            Far = product,
        };
        var scene = new WorldInfinityViewScene(dresser, () => entry.Views, planned.Spec) {
            FitSkyViews = (views, sky, width, height) => FilmInfinity(entry: entry, height: height, sky: sky, views: views, width: width),
        };

        entry.Source = new SdfCompositionFrameSource(dresser: scene, emitters: [new WorldFarProductEmitter(product: product)]);
        m_feedsMoved = true;
        m_infinityEntriesMoved = true;
        return entry;
    }
    private SdfWorldResidency? InfinityResidencyOf(InfinityEntry entry) {
        if (entry.Residency is { } held) { return held; }
        if ((m_viewPipelines is null) || (entry.Source is not { } source) || !entry.Content.MayMake(access: entry.Access.Level)) { return null; }
        entry.Content.Made();
        var residency = new SdfWorldResidency(
            dynamicTransformCapacity: source.DynamicTransformCapacity,
            frameSource: new WorldSessionFrameSource(source, CaptureHostFirst),
            height: ((uint)m_viewDisplayHeight), width: ((uint)m_viewDisplayWidth),
            instanceCapacity: source.InstanceCapacity,
            programWordCapacity: source.ProgramWordCapacity,
            kernels: ViewKernels(), meshLibrary: MeshLibrary, name: entry.Name, pipelines: m_viewPipelines,
            screenSources: new InfinityScreenSources(entry: entry));

        entry.Residency = residency;
        RegisterViewWork(lifetime: residency.WorkLifetime, name: entry.Name, transforms: source.MovedTransforms, work: residency.Work);
        return residency;
    }
    private bool TryResolveInfinityView(string name, out SdfWorldView view) {
        view = default;
        if (!m_infinityOutputs.TryGetValue(key: name, value: out var output) || (InfinityResidencyOf(entry: output.Entry) is not { } residency)) { return false; }
        view = new SdfWorldView(Residency: residency, View: output.Index);
        return true;
    }
    private void ReleaseInfinity(InfinityEntry entry) {
        if (entry.Screens is { } screens) {
            screens.Close(sessions: NestedSessionsOf());
            if (m_nestedOwners.Remove(key: screens)) {
                m_accessBindingsVersion++;
            }
        }
        entry.Access.Close();
        entry.Envelope?.Dispose();
        entry.Emitter.Dispose();
        if (entry.Residency is { } residency) {
            UnregisterViewWork(name: entry.Name);
            residency.Dispose();
        }
        m_feedsMoved = true;
        m_infinityEntriesMoved = true;
    }
    private void ReleaseInfinityRoots() {
        foreach (var entry in m_infinityContents.Values) {
            entry.Roots.Clear();
            ReleaseInfinity(entry: entry);
        }
        m_infinityContents.Clear();
        foreach (var root in m_infinityRoots.Values) { m_renderProbe?.UnregisterInfinityPlan(owner: root); }
        m_infinityRoots.Clear();
        m_infinityEntries.Clear();
        m_infinityEntriesMoved = false;
        ClearInfinityOutputs();
    }
    private readonly List<InfinityEntry> m_infinityEntries = [];
    private bool m_infinityEntriesMoved;

    // Enumerated by many presentation queries. Entry creation and retirement invalidate the traversal in one place.
    private List<InfinityEntry> InfinityEntries() {
        if (!m_infinityEntriesMoved) { return m_infinityEntries; }
        m_infinityEntries.Clear();
        m_infinityEntries.AddRange(collection: m_infinityContents.Values);
        m_infinityEntriesMoved = false;
        return m_infinityEntries;
    }

    private sealed class InfinityRoot(WorldSessionMirror mirror, string instance, int depth, string name) {
        public WorldSessionMirror Mirror { get; } = mirror;
        public string Instance { get; } = instance;
        public int Depth { get; } = depth;
        public string Name { get; } = name;

        public int Epoch { get; set; }

        public int NestingDepth { get; set; } = -1;
        // The far products' version the plan was made at (m_farVersion).
        public long FarVersion { get; set; }
        public WorldInfinityViewPlan Plan { get; set; } = WorldInfinityViewPlan.Empty;
        // The sky section of each mirror the plan was resolved from: the only part of a definition the plan reads.
        public List<(WorldSessionMirror Mirror, WorldRenderSky? Sky)> Skies { get; } = [];
        public List<InfinityObservation> Observations { get; } = [];
        public Dictionary<string, InfinityEntry> Entries { get; } = new(comparer: StringComparer.Ordinal);
        public Dictionary<string, InfinityConsumer> Consumers { get; } = new(comparer: StringComparer.Ordinal);
        public Dictionary<string, WorldInfinityViewPlan> Subplans { get; } = new(comparer: StringComparer.Ordinal);

        // The fit's per-frame questions about this root's entries, as delegates built once.
        public Func<string, bool> Available => (m_available ??= Entries.ContainsKey);
        public Func<string, IReadOnlyList<string>?> ReadsOf => (m_readsOf ??= name => Entries.GetValueOrDefault(key: name)?.Screens?.Reads);

        private Func<string, bool>? m_available;
        private Func<string, IReadOnlyList<string>?>? m_readsOf;

        public WorldInfinityViewPlan PlanOf(string? parent) {
            if (parent is null) { return Plan; }
            if (!Subplans.TryGetValue(key: parent, value: out var plan)) { Subplans.Add(key: parent, value: plan = Plan.Below(parent: parent)); }
            return plan;
        }
    }
    private sealed class InfinityEntry(InfinityRoot root, WorldInfinityView planned, WorldSessionMirror mirror, string instance,
        WorldObservedSession? hosted, string name, WorldSessionSceneEmitter emitter, IReadOnlyList<string> prototypes,
        Func<WorldObservedSession?, WorldObservedSession?> current) {
        // The root that made the entry, which names it and holds its nested cameras' consumers.
        public InfinityRoot Root { get; } = root;
        // Every root that plans the entry's content (JoinInfinity), the maker among them while it does.
        public List<InfinityRoot> Roots { get; } = [];
        public InfinityContent Identity { get; set; }
        public WorldInfinityView Planned { get; } = planned;
        public WorldSessionMirror Mirror { get; } = mirror;
        public string Instance { get; } = instance;
        // The layer's session as the last collect published it.
        public WorldObservedSession? Hosted => current(arg: hosted);
        public string Name { get; } = name;
        public WorldSessionSceneEmitter Emitter { get; } = emitter;
        public IReadOnlyList<string> Prototypes { get; } = prototypes;
        public List<SdfViewSnapshot> Views { get; } = [];
        public Dictionary<InfinityConsumer, int> Indices { get; } = new(comparer: ReferenceEqualityComparer.Instance);

        public IDisposable? Envelope { get; set; }
        public SdfWorldResidency? Residency { get; set; }
        // The far product the entry draws, or null for an entry that draws its world whole or its prototypes.
        public SdfFarProduct? Far { get; init; }
        // The layer's access to its destination and what its residency may render (ContentAccess), both settled once a
        // frame (SettleSessionRoutes) and first as the entry is made.
        public WorldViewerAccess Access { get; } = SettledAccess(hosted: hosted);
        public ContentAccess Content { get; } = new(level: LayerAccessOf(hosted: hosted), mirror: mirror);
        public WorldNestedScreens<SessionFeed>? Screens { get; set; }
        public SdfCompositionFrameSource? Source { get; set; }

        public QualityTier Tier { get; set; } = QualityTier.High;
    }
    private sealed class InfinityConsumer(string name, string? parent, object target, WorldInfinityViewPlan plan) {
        public WorldInfinityViews Fit { get; } = Create(name: name, plan: plan);
        public WorldViewSet Views { get; } = new();
        public string? Parent { get; } = parent;
        public object Target { get; set; } = target;

        public uint Height { get; set; }
        public bool Seen { get; set; }
        // When its target last fitted it (FitInfinity), on the stopwatch: a consumer is made at its first fit.
        public long FittedAt { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public QualityTier Tier { get; set; }
        public SdfViewSnapshot Viewer { get; set; }
        public uint Width { get; set; }

        public GpuWorkSample Sample { get; } = new();

        // The nested viewer tier the fit asks of the root that holds this consumer, built on its first fit.
        public Func<string, QualityTier>? TierOf { get; set; }

        private static WorldInfinityViews Create(string name, WorldInfinityViewPlan plan) {
            var fit = new WorldInfinityViews(consumer: name);

            fit.Apply(plan: plan);
            return fit;
        }
    }
    private readonly record struct InfinitySource(WorldSessionMirror? Mirror, string? Instance, WorldObservedSession? Hosted);
    private readonly record struct InfinityObservation(string Owner, string Layer, WorldObservedSession? Hosted,
        WorldObservationAdmission? Observation, bool Available);
    private readonly record struct InfinityOutput(InfinityEntry Entry, int Index);
    private readonly record struct InfinityContent(SdfFarProduct? Far, WorldSessionMirror Mirror, object? Hosted, InfinityViewSpec Spec, string Prototypes, int ScreenDepth);
    private sealed class InfinityScreenSources(InfinityEntry entry) : ISdfScreenSources {
        public IReadOnlyList<int> Screens => AllScreens;

        public bool Emits(int screen) => WorldScreenBinder.Emits(entry.Screens, screen);
        public float LuminanceOf(int screen) => WorldScreenBinder.LuminanceOf(row: entry.Screens?.RowOf(screen: screen));
        public SdfScreenFace FaceOf(int screen) => WorldScreenBinder.FaceOf(row: entry.Screens?.RowOf(screen: screen));
        public SdfScreenTreatment TreatmentOf(int screen) => WorldScreenBinder.TreatmentOf(material: entry.Screens?.MaterialOf(screen: screen));
        public SourceMapping? MappingOf(int screen) => (((entry.Screens is { } screens) && screens.Mappings.TryGet(mapping: out var mapping, screen: screen)) ? mapping : null);
        public string? ReadOf(int view, int screen) => entry.Screens?.InstanceOf(screen: screen);
    }
}
