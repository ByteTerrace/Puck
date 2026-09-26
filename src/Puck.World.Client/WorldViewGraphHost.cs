using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A non-destructive pointer sample in client pixels, supplied by the host.</summary>
public readonly record struct WorldPipelinePointerSample(Vector2 ClientPosition, bool HasPosition, bool Pressed);
/// <summary>Hosts a world's <c>views.graphs</c> rows on its render-graph runtime: it composes the runtime's instance set
/// from the rows and the default graph it synthesizes, compiles each row's source in the background and installs the
/// graph on the row's instance, places the panes a layout shows and publishes their mappings. Clocks and history are
/// presentation state.</summary>
public sealed partial class WorldViewGraphHost : IRenderGraphPlacements, IDisposable {
    /// <summary>Gets the directory against which authored graph source paths resolve — the same directory the
    /// server's override gate resolves <c>views.graphs</c> rows against. See <see cref="Rebase"/>.</summary>
    public string DocumentDirectory { get; private set; }
    /// <summary>Every graph row with a source that the runtime runs, by its authored name.</summary>
    public IReadOnlyDictionary<string, Entry> Entries => m_entries;
    /// <summary>Gets this frame's footprints: the synthesized root showing the world, then each pane at its slot's
    /// extent. The render root reads this list, which the host rewrites in place every frame.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints => m_footprints;
    /// <summary>The source loader used by both boot and live authoring: a row naming a package directory loads through
    /// the package, and any other row through the ordinary pipeline loader.</summary>
    public ShaderPackager Packager { get; }
    /// <summary>Gets the synthesized root graph the runtime runs, which a live <c>views.post</c> or pane change recomposes,
    /// or <see langword="null"/> when the document names its own root or no runtime is attached.</summary>
    public WorldRootGraph? Synthesized => m_synthesized;
    /// <summary>A non-destructive pointer read, absent in an offscreen host.</summary>
    public Func<WorldPipelinePointerSample>? ReadPointer { get; set; }
    /// <summary>Completed compilation reports, delivered only from the presentation thread.</summary>
    public Action<string, string>? Report { get; set; }

    /// <summary>One instance's presentation controls, pending compilation, and dependency watch.</summary>
    public sealed partial class Entry {
        private readonly Dictionary<string, (DateTime Time, long Length)> m_stamps = new(comparer: PuckPaths.Comparer);

        /// <summary>The currently requested document-relative source.</summary>
        public string Source { get; internal set; } = string.Empty;

        /// <summary>Gets or sets the non-negative rate the instance's time follows the presentation clock at. A change
        /// takes effect from the frame last presented, so the instance's time never jumps.</summary>
        public float ClockScale {
            get => m_clockScale;
            set {
                Rebase();
                m_clockScale = value;
            }
        }

        // The instance's time is a function of the host's one presentation clock, never a clock of its own:
        // m_clockBase + scale × (presented − m_clockAnchor) while running, m_clockBase while paused. Every control
        // re-anchors the mapping at the frame last presented, so a change never moves the time already shown.
        private double m_clockAnchor;
        private bool m_clockAnchored;
        private double m_clockBase;
        private bool m_clockPaused;

        private float m_clockScale = 1f;

        private double m_clockSeconds;
        private double m_presentedSeconds;
        private long m_changedAt;
        private long m_lastPolledAt;
        private long m_retryAt;

        internal BackgroundBuild<CompileOutcome> Compilation { get; } = new();

        // The graph the runtime was last given for the instance, its inputs as they were bound then.
        internal RenderGraphRuntimeGraph? Installed { get; set; }
        internal int PendingSteps { get; set; }
        internal Exception? ReportedSwapError { get; set; }

        /// <summary>The latest console capture awaiting a completion report on the presentation pump.</summary>
        public FrameCaptureRequest? Capture { get; private set; }
        /// <summary>The number of console captures requested since this instance was registered.</summary>
        public int CapturesRequested { get; private set; }
        /// <summary>The failure of the most recently reported capture, or <see langword="null"/> when it was written.</summary>
        public string? LastCaptureError { get; private set; }
        /// <summary>Gets or sets whether time and feedback advancement are paused.</summary>
        public bool ClockPaused {
            get => m_clockPaused;
            set {
                Rebase();
                m_clockPaused = value;
            }
        }
        /// <summary>Gets or sets the instance's time in seconds, the value its passes read as <c>frameGroup.time</c>:
        /// the host's presentation clock mapped through the instance's scale, pauses, steps and resets. Setting it
        /// moves the time the next frame presents from.</summary>
        public double ClockSeconds {
            get => m_clockSeconds;
            set {
                m_clockSeconds = value;
                Rebase();
            }
        }
        /// <summary>Whether a background compilation is still pending installation.</summary>
        public bool IsCompiling => Compilation.IsPending;
        /// <summary>The most recently completed compilation, including any diagnostics.</summary>
        public ShaderPipelineLoadResult? LastCompile { get; internal set; }
        /// <summary>The pointer's position during its most recent press over the instance, in the instance's pixels with
        /// the origin at the top-left corner, or zero before the first press.</summary>
        public Vector2 Pointer { get; set; }
        /// <summary>Whether the pointer was pressed in the previous frame.</summary>
        public bool PointerWasDown { get; set; }
        /// <summary>How many presses the pointer has made over the instance.</summary>
        public uint PointerPresses { get; set; }
        /// <summary>Gets the node the runtime renders the instance through, which the runtime owns.</summary>
        public required ShaderPipelineRenderNode Node { get; init; }
        /// <summary>Gets why the instance can show nothing, or <see langword="null"/> while it has a graph installed or on
        /// its way (a compilation pending, a candidate queued or building). It is its latest compilation's failure, or
        /// the refusal of the candidate its node was given. A slot showing a refused instance places nothing, so the
        /// root draws the world beneath it and a capture of the root never waits on it; a wait on the instance fails
        /// naming the refusal.</summary>
        public string? Refusal {
            get {
                if (
                    IsCompiling ||
                    (Node.Plan is not null) ||
                    Node.HasPendingCandidate ||
                    Node.IsBuildingCandidate
                ) {
                    return null;
                }

                return ((LastCompile is { Status: ShaderPipelineLoadStatus.Failed or ShaderPipelineLoadStatus.Unsupported } failed)
                    ? failed.Message
                    : Node.LastSwapError?.Message);
            }
        }
        /// <summary>The number of dependency changes observed.</summary>
        public int SourceChangeCount { get; private set; }
        /// <summary>The root source being watched, or null when watching is disabled.</summary>
        public string? WatchPath { get; private set; }

        internal void CancelPending() =>
            Compilation.Cancel();
        internal bool PollWatch(long debounceTicks, long pollTicks) {
            var now = Stopwatch.GetTimestamp();

            if ((now - m_lastPolledAt) >= pollTicks) {
                m_lastPolledAt = now;
                // Bound filesystem metadata reads independently of the host's presentation frame rate.
                // Replacing values is safe while enumerating Dictionary on the supported runtime.
                foreach (var (path, before) in m_stamps) {
                    var after = ReadStamp(path: path);

                    if (before == after) { continue; }
                    m_stamps[path] = after;
                    SourceChangeCount++;
                    m_changedAt = now;
                }
            }
            var queuedAt = Math.Max(
                val1: m_changedAt,
                val2: m_retryAt
            );

            if (
                (queuedAt == 0) ||
                ((now - queuedAt) < debounceTicks)
            ) { return false; }
            m_retryAt = 0;
            m_changedAt = 0;
            return true;
        }
        internal void RefreshDependencies() {
            if (WatchPath is not { } root) { return; }
            var retained = new HashSet<string>(comparer: PuckPaths.Comparer) { root };

            if (LastCompile is { } compiled) { retained.UnionWith(other: compiled.Dependencies); }
            foreach (var path in retained) {
                m_stamps.TryAdd(
                key: path,
                value: ReadStamp(path: path)
            );
            }
            foreach (var path in m_stamps.Keys.ToArray()) {
                if (!retained.Contains(item: path)) { m_stamps.Remove(key: path); }
            }
            // Preserve existing stamps and debounce state: an edit during compilation must schedule another load.
        }
        internal void ScheduleRetry() => m_retryAt = Stopwatch.GetTimestamp();

        // Re-anchors the time mapping at the frame last presented, holding the time it showed.
        private void Rebase() {
            m_clockBase = m_clockSeconds;
            m_clockAnchor = m_presentedSeconds;
        }
        private static (DateTime, long) ReadStamp(string path) {
            try {
                var info = new FileInfo(fileName: path);

                return (info.Exists
                    ? (info.LastWriteTimeUtc, info.Length)
                    : (DateTime.MinValue, -1L)
                );
            } catch (IOException) {
                return (DateTime.MinValue, -1L);
            } catch (UnauthorizedAccessException) {
                return (DateTime.MinValue, -1L);
            }
        }

        /// <summary>Presents this instance once for a produced host frame at the host's presentation clock and returns
        /// the seconds its time moved since the frame before. A running instance's time follows the clock at its scale;
        /// a paused one, or one not yet ready, holds, except that a pending step advances it by exactly one
        /// development frame.</summary>
        /// <param name="presentedSeconds">The presentation clock the frame presents at, in seconds: the host state
        /// mirror's presented engine tick.</param>
        /// <returns>The seconds the instance's time moved, which its passes read as <c>frameGroup.timeDelta</c>.</returns>
        public double AdvanceClock(double presentedSeconds) {
            var previous = m_clockSeconds;

            Node.Paused = (m_clockPaused || (m_clockScale == 0));
            if (
                !m_clockAnchored ||
                (presentedSeconds < m_clockAnchor)
            ) {
                m_clockAnchor = presentedSeconds;
                m_clockBase = m_clockSeconds;
                m_clockAnchored = true;
            }
            if (!Node.IsReady) {
                m_clockAnchor = presentedSeconds;
            } else if (PendingSteps > 0) {
                PendingSteps--;
                m_clockBase = (m_clockSeconds + StepSeconds);
                m_clockAnchor = presentedSeconds;
                m_clockSeconds = m_clockBase;
                Node.Step();
            } else if (Node.Paused) {
                m_clockAnchor = presentedSeconds;
            } else {
                m_clockSeconds = (m_clockBase + (m_clockScale * (presentedSeconds - m_clockAnchor)));
            }

            m_presentedSeconds = presentedSeconds;

            return (m_clockSeconds - previous);
        }
        /// <summary>Resets time, pending steps, and GPU feedback to their initial values.</summary>
        public void Reset() {
            ClockSeconds = 0;
            PendingSteps = 0;
            Node.Reset();
        }

        /// <summary>The seconds one <see cref="Step"/> advances the instance's time by: one frame at the standard
        /// development rate of 60 hertz.</summary>
        public const double StepSeconds = (1.0 / 60.0);

        /// <summary>Pauses and schedules exactly one logical frame at the standard development rate.</summary>
        public void Step() {
            ClockPaused = true;
            PendingSteps = checked((PendingSteps + 1));
        }
        /// <summary>Stops watching and discards any pending debounce event.</summary>
        public void Unwatch() {
            WatchPath = null;
            m_stamps.Clear();
            m_lastPolledAt = 0;
            m_changedAt = 0;
        }
        /// <summary>Watches the source and the last compilation's dependencies.</summary>
        public void Watch(string path) {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            Unwatch();
            WatchPath = path;
            RefreshDependencies();
        }
    }

    // One background compilation: the loader's result and, for a compiled candidate, the read of the source it was
    // compiled from, taken before and after the load so an edit during compilation retries instead of mislabeling.
    internal sealed record CompileOutcome(ShaderPipelineLoadResult Result, ShaderPipelineSource? Source);

    /// <summary>The name <see cref="Report"/> gives a report about the instance set as a whole.</summary>
    public const string SetReportName = "render-graph";
    /// <summary>The source watch's quiet period before requesting compilation.</summary>
    public const int WatchDebounceMilliseconds = 150;
    /// <summary>The minimum interval between dependency metadata polls, independent of presentation cadence.</summary>
    public const int WatchPollMilliseconds = 50;

    private readonly Dictionary<string, Entry> m_entries = new(comparer: StringComparer.Ordinal);
    private readonly List<RenderGraphFootprint> m_footprints = [];
    private readonly Dictionary<string, RenderGraphPlacement> m_placements = new(comparer: StringComparer.Ordinal);

    private Func<IReadOnlyList<string>, int, IReadOnlyList<WorldViewPostPass>?, WorldRootGraph>? m_compose;
    private bool m_disposed;
    private WorldViewDefaults? m_lastViews;
    // The last refusal Reconcile reported, so a section it keeps retrying reports each refusal once.
    private string? m_refusal;
    private IRenderGraphInstances? m_runtime;
    private WorldRootGraph? m_synthesized;

    /// <summary>Creates a host using the shared source loader and the world's document directory.</summary>
    public WorldViewGraphHost(ShaderPackager packager, string documentDirectory) {
        ArgumentNullException.ThrowIfNull(packager);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);
        Packager = packager;
        DocumentDirectory = Path.GetFullPath(path: documentDirectory);
    }

    /// <summary>Composes a runtime's instance set from a document's <c>views</c> section: the synthesized default graph
    /// (the world producer, then every row, then the root that reads the world and the panes) when the section names no
    /// <c>views.root</c>, or the rows alone, rooted where <c>views.root</c> says, when it does.</summary>
    /// <param name="views">The document's <c>views</c> section.</param>
    /// <param name="synthesized">The default graph, or <see langword="null"/> when the section names its own root.</param>
    /// <param name="passes">The passes one render of a row's graph records.</param>
    /// <param name="set">The instances, when this returns <see langword="true"/>.</param>
    /// <param name="graphs">Each instance's graph, parallel to <paramref name="set"/>: the synthesized root's, and
    /// <see langword="null"/> for the world producer and every row.</param>
    /// <param name="root">The instance the display shows.</param>
    /// <param name="reason">Why the instances were refused.</param>
    /// <returns><see langword="true"/> when the instances form a valid set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> or <paramref name="passes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="synthesized"/> is <see langword="null"/> and the section names
    /// no root.</exception>
    public static bool TryCompose(WorldViewDefaults views, WorldRootGraph? synthesized, Func<WorldViewGraph, int> passes, [NotNullWhen(returnValue: true)] out RenderGraphInstanceSet? set, out IReadOnlyList<RenderGraphRuntimeGraph?> graphs, out string root, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: views);
        ArgumentNullException.ThrowIfNull(argument: passes);

        var rows = WorldViewGraphs.Instances(
            graphs: (views.Graphs ?? []),
            passes: passes
        );
        List<RenderGraphInstance> instances;
        var composed = new List<RenderGraphRuntimeGraph?>();

        if (views.Root is { } authored) {
            instances = [.. rows];
            composed.AddRange(collection: Enumerable.Repeat<RenderGraphRuntimeGraph?>(count: rows.Count, element: null));
            root = authored;
        } else {
            if (synthesized is null) {
                throw new ArgumentException(
                    message: "A section that names no root renders the synthesized default graph, and none was given.",
                    paramName: nameof(synthesized)
                );
            }

            var synthesizedGraphs = synthesized.Graphs();

            // The world producers first, so the runtime renders the one that renders every view before the ones that hand
            // out later views' outputs.
            var producers = synthesized.Producers.Count;

            instances = [.. synthesized.Producers, .. rows, .. synthesized.Instances.Skip(count: producers)];
            composed.AddRange(collection: synthesizedGraphs.Take(count: producers));
            composed.AddRange(collection: Enumerable.Repeat<RenderGraphRuntimeGraph?>(count: rows.Count, element: null));
            composed.AddRange(collection: synthesizedGraphs.Skip(count: producers));
            root = synthesized.Root;
        }

        graphs = composed;

        if (!RenderGraphInstanceSet.TryCreate(
            instances: instances,
            refusal: out var refusal,
            set: out set
        )) {
            reason = refusal.Message;

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Drives a runtime from here on: the next <see cref="Reconcile"/> composes the document's instance set
    /// onto it.</summary>
    /// <param name="runtime">The runtime, built from the set <see cref="TryCompose"/> composed for the booted
    /// document.</param>
    /// <param name="compose">Synthesizes the default graph over the panes a document's layouts place, the views they
    /// compose (<see cref="WorldRootGraph.ViewsOf"/>) and the document's current <c>views.post</c> passes.</param>
    /// <param name="synthesized">The default graph the runtime was built with, or <see langword="null"/> when the booted
    /// document names its own root.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="compose"/> is
    /// <see langword="null"/>.</exception>
    public void Attach(IRenderGraphInstances runtime, Func<IReadOnlyList<string>, int, IReadOnlyList<WorldViewPostPass>?, WorldRootGraph> compose, WorldRootGraph? synthesized) {
        ArgumentNullException.ThrowIfNull(argument: runtime);
        ArgumentNullException.ThrowIfNull(argument: compose);

        m_compose = compose;
        m_lastViews = null;
        m_runtime = runtime;
        m_synthesized = synthesized;
        ResetFootprints();
    }
    /// <summary>Returns the frame values every graph instance presents at a frame before its own pointer, camera and
    /// clock, read from the state mirror, the one presentation clock: the mirror's delivered engine tick as
    /// <c>tick</c>, and its presented engine tick at the frame's interpolation fraction, in seconds, as <c>time</c>,
    /// with the seconds it moved since the frame before as <c>timeDelta</c>. Nothing here reads a wall clock, so a
    /// frame at a given delivered tick and fraction presents the same bytes on every run.</summary>
    /// <param name="mirror">The state mirror the frame presents.</param>
    /// <param name="fraction">The frame's interpolation fraction in <c>[0, 1]</c>; an offscreen presentation passes
    /// one.</param>
    /// <param name="previousSeconds">The time the frame before presented, in seconds; a later time never reads a
    /// negative delta.</param>
    /// <returns>The frame values, with no pointer and no paired camera.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public static ShaderFrameValues PresentedFrame(WorldStateMirror mirror, float fraction, double previousSeconds) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        var seconds = (mirror.PresentedEngineTick(fraction: fraction) / EngineTicks.PerSecond);

        return new ShaderFrameValues(
            CameraFov: 0f,
            CameraPosition: Vector3.Zero,
            CameraTarget: Vector3.Zero,
            CameraUp: Vector3.Zero,
            Pointer: Vector2.Zero,
            PointerDown: false,
            PointerPresses: 0,
            StateTick: mirror.Tick,
            Tick: mirror.EngineTick,
            Time: seconds,
            TimeDelta: Math.Max(
                val1: 0d,
                val2: (seconds - previousSeconds)
            )
        );
    }
    /// <summary>Hands every graph instance the runtime renders for this host the frame values the frame presents: each
    /// synthesized instance's node and each row's. A pane the frame shows is handed its own pointer, camera and clock
    /// over them afterwards. Does nothing before a runtime is attached.</summary>
    /// <param name="frame">The frame values: the presented tick and presentation time, with no pointer and no paired
    /// camera.</param>
    public void Present(in ShaderFrameValues frame) {
        if (m_runtime is not { } runtime) {
            return;
        }

        if (m_synthesized is { } synthesized) {
            var instances = synthesized.Instances;

            for (var index = 0; (index < instances.Count); index++) {
                if (runtime.NodeOf(instance: instances[index].Name) is { } node) {
                    node.Frame = frame;
                }
            }
        }

        foreach (var entry in m_entries.Values) {
            entry.Node.Frame = frame;
        }
    }
    /// <summary>Starts a frame before the runtime schedules it: reconciles the accepted <c>views</c> section, installs
    /// complete candidates and polls dependency watches, and clears the previous frame's placements and cameras, leaving
    /// the footprints the synthesized root always shows. The panes published last stay published until
    /// <see cref="PublishPanes"/> replaces them.</summary>
    /// <param name="views">The accepted document's <c>views</c> section.</param>
    public void BeginFrame(WorldViewDefaults views) {
        Reconcile(views: views);
        PumpWatches();
        ResetFootprints();
        m_placements.Clear();
        m_cameras.Clear();
    }
    /// <summary>Places a pane this frame: the synthesized root shows the instance inside a normalized rect of the display,
    /// renders it at that rect's extent, and reconstructs it at the given sharpness. An instance the root does not place
    /// is ignored, and so is a refused one (<see cref="Entry.Refusal"/>): the root draws the world beneath its slot, and
    /// nothing the root shows waits on it.</summary>
    /// <param name="instance">The <c>views.graphs</c> instance the slot names.</param>
    /// <param name="region">The slot's normalized rect.</param>
    /// <param name="sharpness">The reconstruction's sharpness, from 0 (bilinear) to 1 (clamped Catmull-Rom).</param>
    /// <returns><see langword="true"/> when the root places the instance this frame.</returns>
    public bool Place(string instance, NormalizedRect region, float sharpness) {
        if (
            (m_synthesized is not { Plan: not null } synthesized) ||
            !synthesized.Panes.Contains(value: instance) ||
            m_placements.ContainsKey(key: instance) ||
            (m_entries.TryGetValue(
                key: instance,
                value: out var entry
            ) && (entry.Refusal is not null))
        ) {
            return false;
        }

        m_placements.Add(
            key: instance,
            value: new RenderGraphPlacement(
                Height: region.Height,
                Left: region.X,
                Sharpness: sharpness,
                Shown: true,
                Top: region.Y,
                Width: region.Width
            )
        );
        m_footprints.Add(item: new RenderGraphFootprint(
            Consumer: WorldViewGraphs.MainInstance,
            Height: region.Height,
            Producer: instance,
            Width: region.Width
        ));

        return true;
    }
    /// <summary>Places a view of the world this frame: the synthesized root reads the view's producer at its rect's
    /// extent at its render scale, and, when the view is shown, reconstructs its output into the rect at the given
    /// sharpness. The first view is also the base the root draws everything over, so it is read whether shown or not. A
    /// view the synthesized root does not place (one past its <see cref="WorldRootGraph.Views"/>, or any view under a
    /// graph that places none) is ignored.</summary>
    /// <param name="view">The 0-based view.</param>
    /// <param name="region">The view's normalized rect.</param>
    /// <param name="renderScale">The view's render scale in (0, 1]; any other value renders native.</param>
    /// <param name="sharpness">The reconstruction's sharpness, from 0 (bilinear) to 1 (clamped Catmull-Rom).</param>
    /// <param name="shown">Whether the root draws the view's output into its rect this frame.</param>
    /// <param name="uncovered">Whether part of the display lies outside everything the root shows this frame, so the
    /// first view's place pass, when the view is not shown, writes the letterbox color everywhere rather than standing
    /// for its base (<see cref="RenderGraphPlacement.Uncovered"/>).</param>
    /// <returns><see langword="true"/> when the root places the view this frame.</returns>
    public bool PlaceView(int view, NormalizedRect region, float renderScale, float sharpness, bool shown, bool uncovered) {
        if (
            (m_synthesized is not { Plan: not null } synthesized) ||
            (((uint)view) >= ((uint)synthesized.ViewPasses.Count))
        ) {
            return false;
        }

        var scale = (((renderScale > 0f) && (renderScale < 1f))
            ? renderScale
            : 1f);

        m_placements[synthesized.ViewPasses[view]] = new RenderGraphPlacement(
            Uncovered: uncovered,
            Height: region.Height,
            Left: region.X,
            Sharpness: sharpness,
            Shown: shown,
            Top: region.Y,
            Width: region.Width
        );

        if (shown || (view == 0)) {
            m_footprints.Add(item: new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: (region.Height * scale),
                Producer: synthesized.Producers[view].Name,
                Width: (region.Width * scale)
            ));
        }

        return true;
    }
    /// <summary>Places every view a composed frame of the world rendered (<see cref="PlaceView"/>), each in its rect at
    /// its render scale, and records each view's camera for its producer (<see cref="SetCamera"/>). A view is shown once the world has rendered it, except a lone view covering the whole display at
    /// native scale, which is never shown, so the root stands for the world itself. Before the world has composed a frame
    /// there are no views, but the world must still be scheduled, since it composes inside its own frame, so the first
    /// view is placed, not shown, over the whole display at native scale, which it renders at until its first frame names
    /// its views. The display counts as covered only when one rect covers it whole: a lone whole-display view, a shown
    /// view over the whole display, or a pane that covers it (<paramref name="panesCover"/>); otherwise pixels no rect
    /// covers show the letterbox color, even while the first view is not shown.</summary>
    /// <param name="views">The views of the world's last composed frame, in view order.</param>
    /// <param name="sharpness">The reconstruction's sharpness, from 0 (bilinear) to 1 (clamped Catmull-Rom).</param>
    /// <param name="rendered">Whether the world has rendered a view into its output, by 0-based view, or
    /// <see langword="null"/> when no view has an output yet.</param>
    /// <param name="panesCover">Whether a pane the root shows this frame covers the whole display.</param>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public void PlaceViews(IReadOnlyList<SdfViewSnapshot> views, float sharpness, Func<int, bool>? rendered, bool panesCover) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var whole = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        if (views.Count == 0) {
            _ = PlaceView(
                region: whole,
                renderScale: 1f,
                sharpness: sharpness,
                shown: false,
                uncovered: !panesCover,
                view: 0
            );

            return;
        }

        var lone = (
            (views.Count == 1) &&
            (views[0].Region == whole) &&
            !((views[0].RenderScale > 0f) && (views[0].RenderScale < 1f))
        );
        var covered = (panesCover || lone);

        for (var view = 0; (view < views.Count); view++) {
            covered |= (
                (views[view].Region == whole) &&
                Shows(
                    lone: lone,
                    rendered: rendered,
                    view: view
                )
            );
        }
        for (var view = 0; (view < views.Count); view++) {
            var snapshot = views[view];

            if (
                (m_synthesized is { } synthesized) &&
                (view < synthesized.Producers.Count)
            ) {
                SetCamera(
                    camera: snapshot.Camera,
                    instance: synthesized.Producers[view].Name
                );
            }

            _ = PlaceView(
                uncovered: !covered,
                region: snapshot.Region,
                renderScale: snapshot.RenderScale,
                sharpness: sharpness,
                shown: Shows(
                    lone: lone,
                    rendered: rendered,
                    view: view
                ),
                view: view
            );
        }
    }

    // Whether a view of a composed frame is shown: once rendered, unless it is the lone whole-display view.
    private static bool Shows(bool lone, Func<int, bool>? rendered, int view) => (!lone && (rendered?.Invoke(arg: view) ?? false));

    /// <inheritdoc/>
    /// <remarks>A pane or view of the synthesized root the host did not place this frame is not shown, so its pass
    /// draws nothing.</remarks>
    public bool TryGet(string instance, string pass, out RenderGraphPlacement placement) {
        if (
            !string.Equals(
                a: instance,
                b: WorldViewGraphs.MainInstance,
                comparisonType: StringComparison.Ordinal
            ) ||
            (m_synthesized is not { } synthesized) ||
            (!synthesized.Panes.Contains(value: pass) && !synthesized.ViewPasses.Contains(value: pass))
        ) {
            placement = default;

            return false;
        }

        if (!m_placements.TryGetValue(
            key: pass,
            value: out placement
        )) {
            placement = default;
        }

        return true;
    }
    /// <summary>Rebases every future relative source resolution onto a newly loaded document's own directory — a
    /// <c>world.load</c>/<c>world.reload</c> that installs a document from another directory moves what a row's
    /// relative <c>source</c> resolves against, the same directory the server's override gate begins reading rows
    /// from at that same moment. Does not retroactively re-resolve an already-compiled instance; a live
    /// <see cref="QueueCompile"/> after this call is what reads the new directory.</summary>
    /// <param name="documentDirectory">The newly loaded document's directory.</param>
    public void Rebase(string documentDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentDirectory);
        DocumentDirectory = Path.GetFullPath(path: documentDirectory);
    }
    /// <summary>Cancels pending compilations; the runtime retains ownership of the GPU instances.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var entry in m_entries.Values) {
            entry.CancelPending();
        }
    }
    /// <summary>Installs complete candidates and polls dependency watches before this host frame renders.</summary>
    public void PumpWatches() {
        if (m_disposed) { return; }
        var debounce = ((Stopwatch.Frequency * WatchDebounceMilliseconds) / 1000);
        var poll = ((Stopwatch.Frequency * WatchPollMilliseconds) / 1000);

        foreach (var (name, entry) in m_entries) {
            if (entry.Capture is { Completion.IsCompleted: true } capture) {
                var result = capture.Completion.GetAwaiter().GetResult();

                entry.CompleteCapture(error: (result.Succeeded
                    ? null
                    : result.Error!.Message));
                Report?.Invoke(
                    name,
                    (result.Succeeded
                    ? $"captured {result.Path}"
                    : $"capture failed: {result.Error!.Message}")
                );
            }
            if (!ReferenceEquals(
                objA: entry.ReportedSwapError,
                objB: entry.Node.LastSwapError
            )) {
                entry.ReportedSwapError = entry.Node.LastSwapError;
                if (entry.ReportedSwapError is { } error) {
                    Report?.Invoke(
                    name,
                    $"GPU candidate refused: {error.Message}"
                );
                }
            }
            if (entry.Compilation.TryTake(
                error: out var compileError,
                result: out var compiled
            )) {
                var result = (compiled?.Result ?? new ShaderPipelineLoadResult(
                    Dependencies: [],
                    Message: compileError!.Message,
                    Pipeline: null,
                    Status: ShaderPipelineLoadStatus.Failed
                ));

                entry.LastCompile = result;
                if (result.Pipeline is { } pipeline) {
                    // A compiled candidate not yet installed — queued for the next frame, or still building — is replaced
                    // by a newer one.
                    var held = entry.Node.HasPendingCandidate;

                    try {
                        if (!Install(
                            name: name,
                            pipeline: pipeline,
                            reason: out var refused
                        )) {
                            result = result with { Message = refused, Pipeline = null, Status = ShaderPipelineLoadStatus.Failed };
                            entry.LastCompile = result;
                        } else {
                            entry.Candidate = ((compiled?.Source is { } source)
                                ? (pipeline.Plan, source)
                                : null);
                            if (held) {
                                Report?.Invoke(
                                    name,
                                    "superseded: compiled candidate"
                                );
                            }
                        }
                    } catch (Exception exception) when ((exception is InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)) {
                        result = result with { Message = exception.Message, Pipeline = null, Status = ShaderPipelineLoadStatus.Failed };
                        entry.LastCompile = result;
                    }
                }
                entry.RefreshDependencies();
                if (result.Status == ShaderPipelineLoadStatus.Retry) { entry.ScheduleRetry(); }
                Report?.Invoke(
                    name,
                    ((result.Status == ShaderPipelineLoadStatus.Unsupported)
                    ? $"unsupported: {result.Message}"
                    : result.Message)
                );
            }
            entry.Synchronize();
            if (entry.PollWatch(
                debounceTicks: debounce,
                pollTicks: poll
            )) {
                QueueCompile(
                name: name,
                source: entry.Source
            );
            }
        }
    }
    /// <summary>Schedules a complete candidate compilation. A newer request supersedes an older result: a compilation
    /// still pending is canceled and never installed, and <see cref="Report"/> says so as <c>superseded: compilation</c>.
    /// A compiled candidate not yet installed, queued for the next frame or with its pipelines still building, is
    /// likewise replaced when the newer one compiles (<c>superseded: compiled candidate</c>).</summary>
    public void QueueCompile(string name, string source) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        var entry = m_entries[name];
        var superseded = entry.IsCompiling;

        entry.CancelPending();
        if (superseded) {
            Report?.Invoke(
                name,
                "superseded: compilation"
            );
        }
        entry.Source = source;
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: DocumentDirectory,
            path: source,
            reason: out var unresolved,
            resolved: out var resolved
        )) {
            entry.LastCompile = new ShaderPipelineLoadResult(
                Dependencies: [],
                Message: unresolved,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Failed
            );
            Report?.Invoke(
                name,
                unresolved
            );
            return;
        }
        if (
            (entry.WatchPath is { } watched) &&
            !string.Equals(
            a: watched,
            b: resolved,
            comparisonType: PuckPaths.Comparison
        )
        ) { entry.Watch(path: resolved); }
        entry.Compilation.Start(build: token => {
            try {
                _ = ShaderPipelineSource.TryRead(
                    name: name,
                    path: resolved,
                    reason: out _,
                    source: out var before
                );

                var loaded = Packager.LoadSource(
                    cancellationToken: token,
                    name: name,
                    path: resolved
                );

                if (loaded.Status != ShaderPipelineLoadStatus.Compiled) {
                    return new CompileOutcome(
                        Result: loaded,
                        Source: null
                    );
                }
                // The identity a commit carries is the source this candidate was compiled from; a source that moved
                // while the loader ran retries the whole pipeline, as the loader's own source check does.
                if (
                    !ShaderPipelineSource.TryRead(
                    name: name,
                    path: resolved,
                    reason: out _,
                    source: out var after
                ) ||
                    !string.Equals(
                    a: before?.SourceIdentity,
                    b: after.SourceIdentity,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return new CompileOutcome(
                        Result: new ShaderPipelineLoadResult(
                            Dependencies: loaded.Dependencies,
                            Message: "Source changed during compilation; retrying the complete pipeline.",
                            Pipeline: null,
                            Status: ShaderPipelineLoadStatus.Retry
                        ),
                        Source: null
                    );
                }

                return new CompileOutcome(
                    Result: loaded,
                    Source: after
                );
            } catch (OperationCanceledException) {
                return Failed(
                    message: "compilation superseded",
                    status: ShaderPipelineLoadStatus.Failed
                );
            } catch (ShaderToolMissingException exception) {
                return Failed(
                    message: exception.Message,
                    status: ShaderPipelineLoadStatus.Unsupported
                );
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)) {
                return Failed(
                    message: exception.Message,
                    status: ShaderPipelineLoadStatus.Failed
                );
            }

            CompileOutcome Failed(string message, ShaderPipelineLoadStatus status) => new(
                Result: new ShaderPipelineLoadResult(
                    Dependencies: [resolved],
                    Message: message,
                    Pipeline: null,
                    Status: status
                ),
                Source: null
            );
        });
    }
    /// <summary>Reconciles the accepted <c>views</c> section onto the runtime before it schedules a frame: the instance
    /// set follows the section's rows and the panes its layouts place, every surviving instance keeps its node and
    /// graph, a row with a new source compiles, and a removed row's instance retires. A row whose inputs moved keeps its
    /// installed graph, rebound to them, only while it stays a graph instance and that graph declares exactly the
    /// versions the new inputs name; otherwise its source compiles anew. Refused mutations never reach here, and a set
    /// the runtime refuses leaves the running one in place, reported by name once, and is tried again on the next call.
    /// Does nothing before a runtime is attached.</summary>
    /// <param name="views">The accepted document's <c>views</c> section.</param>
    public void Reconcile(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        if (
            m_disposed ||
            (m_runtime is not { } runtime) ||
            ReferenceEquals(
                objA: m_lastViews,
                objB: views
            )
        ) {
            return;
        }

        ReconcileChanged(
            runtime: runtime,
            views: views
        );
    }
    /// <summary>Looks up an instance the host runs without creating one.</summary>
    public bool TryGet(string name, out Entry entry) => m_entries.TryGetValue(
        key: name,
        value: out entry!
    );

    // Reconciles a views section that differs from the one last accepted. Apart from Reconcile, so the closures its
    // rebinding captures are allocated only when the section moved, never on a steady frame.
    private void ReconcileChanged(IRenderGraphInstances runtime, WorldViewDefaults views) {
        var synthesized = m_synthesized;

        if (views.Root is null) {
            var panes = WorldRootGraph.PanesOf(views: views);
            var composedViews = WorldRootGraph.ViewsOf(views: views);

            if (
                (synthesized is null) ||
                (synthesized.Views != composedViews) ||
                !synthesized.Panes.SequenceEqual(second: panes, comparer: StringComparer.Ordinal) ||
                !synthesized.Post.SequenceEqual(second: (views.Post ?? []))
            ) {
                try {
                    synthesized = m_compose!(arg1: panes, arg2: composedViews, arg3: views.Post);
                } catch (WorldRootGraphRefusedException exception) {
                    ReportRefusal(reason: exception.Message);

                    return;
                }
            }
        } else {
            synthesized = null;
        }

        if (!TryCompose(
            graphs: out var graphs,
            passes: PassesOf,
            reason: out var reason,
            root: out var root,
            set: out var set,
            synthesized: synthesized,
            views: views
        )) {
            ReportRefusal(reason: reason);

            return;
        }

        // A synthesized root this host already runs keeps the graph it has installed.
        if (ReferenceEquals(
            objA: synthesized,
            objB: m_synthesized
        )) {
            graphs = [.. graphs.Select(selector: (graph, index) => (((synthesized is not null) && string.Equals(
                a: set.Instances[index].Name,
                b: WorldViewGraphs.MainInstance,
                comparisonType: StringComparison.Ordinal
            ))
                ? null
                : graph))];
        }

        // A row whose inputs moved rebinds its installed graph to them in this same reconfiguration, whether or not its
        // source moved too: the set's reads follow the inputs, so the bindings it has may name a producer the instance no
        // longer reads. The runtime rebinds a kept pipeline without building it again. Only a graph instance whose
        // installed graph declares exactly the versions the new inputs bind is rebound: an instance that became a
        // package, or inputs naming a version the installed graph lacks, leave the slot empty for the new source.
        graphs = [.. graphs.Select(selector: (graph, index) => (((graph is null) &&
            (set.Instances[index].Kind == RenderGraphInstanceKind.Graph) &&
            m_entries.TryGetValue(
                key: set.Instances[index].Name,
                value: out var entry
            ) &&
            (entry.Installed is { } installed) &&
            (WorldDefinitionRows.FindGraph(
                graphs: views.Graphs,
                name: entry.Name
            ) is { } row) &&
            !installed.Inputs.SequenceEqual(second: InputsOf(row: row)) &&
            DeclaresExactly(
                graph: installed,
                inputs: InputsOf(row: row)
            ))
            ? (installed with { Inputs = InputsOf(row: row) })
            : graph))];

        if (!runtime.TryReconfigure(
            graphs: graphs,
            refusal: out var refusal,
            root: root,
            set: set
        )) {
            ReportRefusal(reason: $"{refusal.Code}: {refusal.Message}");

            return;
        }

        m_lastViews = views;
        m_refusal = null;
        m_synthesized = synthesized;

        for (var index = 0; (index < graphs.Count); index++) {
            if (
                (graphs[index] is { } rebound) &&
                m_entries.TryGetValue(
                    key: set.Instances[index].Name,
                    value: out var entry
                ) &&
                (entry.Installed is not null)
            ) {
                entry.Installed = rebound;
            }
        }

        var rows = (views.Graphs ?? []);
        var desiredNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            if (
                (row.Source is null) ||
                (runtime.NodeOf(instance: row.Name) is not { } node)
            ) {
                continue;
            }

            desiredNames.Add(item: row.Name);

            if (
                !m_entries.TryGetValue(
                    key: row.Name,
                    value: out var entry
                ) ||
                !ReferenceEquals(
                    objA: entry.Node,
                    objB: node
                )
            ) {
                entry?.CancelPending();
                entry = new Entry { Name = row.Name, Node = node, Owner = this };
                m_entries[row.Name] = entry;
            }

            entry.Adopt(row: row);

            if (!string.Equals(
                a: entry.Source,
                b: row.Source,
                comparisonType: StringComparison.Ordinal
            )) {
                QueueCompile(
                    name: row.Name,
                    source: row.Source
                );
            }
        }

        foreach (var name in m_entries.Keys.ToArray()) {
            if (desiredNames.Contains(item: name)) { continue; }
            m_entries[name].CancelPending();
            m_entries.Remove(key: name);
        }
    }
    // Installs a compiled graph on its row's instance, binding each input the row declares to the instance it names.
    private bool Install(string name, CompiledShaderPipeline pipeline, out string reason) {
        if (
            (m_runtime is not { } runtime) ||
            (WorldDefinitionRows.FindGraph(
                graphs: m_lastViews?.Graphs,
                name: name
            ) is not { } row)
        ) {
            reason = $"no views.graphs row named '{name}' runs on the render graph";

            return false;
        }

        var graph = new RenderGraphRuntimeGraph(
            Inputs: InputsOf(row: row),
            Pipeline: pipeline
        );

        if (!runtime.TryInstall(
            graph: graph,
            instance: name,
            refusal: out var refusal
        )) {
            reason = $"{refusal.Code}: {refusal.Message}";

            return false;
        }

        m_entries[name].Installed = graph;
        reason = string.Empty;

        return true;
    }
    private static RenderGraphRuntimeInput[] InputsOf(WorldViewGraph row) => [.. (row.Inputs ?? []).Select(selector: static input => new RenderGraphRuntimeInput(
        Producer: input.Instance,
        Version: input.Resource
    ))];
    // The passes a row's instance is priced at: its compiled graph's, or one before it has compiled.
    private int PassesOf(WorldViewGraph row) => ((m_entries.TryGetValue(
        key: row.Name,
        value: out var entry
    ) && (entry.LastCompile?.Pipeline is { } pipeline))
        ? pipeline.Plan.Passes.Count
        : 1);
    // Whether a graph's external versions are exactly the ones a list of inputs binds, so the runtime can rebind it to
    // them.
    private static bool DeclaresExactly(RenderGraphRuntimeGraph graph, IReadOnlyList<RenderGraphRuntimeInput> inputs) {
        var external = graph.Pipeline.Plan.Storages
            .Where(predicate: static storage => storage.IsExternal)
            .Select(selector: static storage => storage.Name)
            .ToHashSet(comparer: StringComparer.Ordinal);
        var bound = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var input in inputs) {
            if (
                (input.Version is not { } version) ||
                !external.Contains(item: version) ||
                !bound.Add(item: version)
            ) {
                return false;
            }
        }

        return (bound.Count == external.Count);
    }
    // Reports a refused section once: Reconcile tries the section again on every call until it is accepted or replaced.
    private void ReportRefusal(string reason) {
        if (string.Equals(
            a: reason,
            b: m_refusal,
            comparisonType: StringComparison.Ordinal
        )) {
            return;
        }

        m_refusal = reason;
        Report?.Invoke(
            SetReportName,
            $"refused: {reason}"
        );
    }
    private void ResetFootprints() {
        m_footprints.Clear();

        if (m_synthesized is { } synthesized) {
            m_footprints.AddRange(collection: synthesized.Footprints);
        }
    }
}
