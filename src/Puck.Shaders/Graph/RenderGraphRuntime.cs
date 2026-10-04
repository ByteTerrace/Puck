using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One edge's binding in a consumer's graph: the external version a producer instance's output is bound
/// to.</summary>
/// <param name="Version">The consumer graph's external version (<see cref="ShaderPipelineInitialization.External"/>).</param>
/// <param name="Producer">The instance whose output it binds; the consumer's instance declares a read of it.</param>
public readonly record struct RenderGraphRuntimeInput(string Version, string Producer);
/// <summary>The graph one instance runs, and where its reads land in it.</summary>
/// <param name="Pipeline">The planned graph with its compiled shader passes. Its default output
/// (<see cref="ShaderPipelinePlan.DefaultOutput"/>) is what the instance's consumers read.</param>
/// <param name="Inputs">Every external version of the graph, each bound to exactly one producer's output. An input bound
/// to the instance itself reads its own previous output, as the scheduler reads every self-read.</param>
public sealed record RenderGraphRuntimeGraph(CompiledShaderPipeline Pipeline, IReadOnlyList<RenderGraphRuntimeInput> Inputs);
/// <summary>Why a runtime refused the graphs it was given.</summary>
public enum RenderGraphRuntimeRefusalCode : byte {
    /// <summary>The graphs are not one per instance of the set.</summary>
    GraphCount = 1,
    /// <summary>The root names no instance whose output is an image.</summary>
    Root = 2,
    /// <summary>An instance's graph publishes a default output of another kind than the instance declares.</summary>
    OutputKind = 3,
    /// <summary>A package pass names a package no recorder serves.</summary>
    PackageUnserved = 4,
    /// <summary>An input names a version that is not one of the graph's external versions, binds one twice, or leaves
    /// one unbound.</summary>
    InputVersion = 5,
    /// <summary>An input names a producer its instance does not read.</summary>
    InputProducer = 6,
    /// <summary>An input binds a version of another kind than its producer's output.</summary>
    InputKind = 7,
    /// <summary>An input binds a buffer version larger than its producer's buffer.</summary>
    InputSize = 8,
    /// <summary>An external instance was given a graph, declares an output that is not an image, or names a package no
    /// external producer serves.</summary>
    ExternalProducer = 9,
    /// <summary>Instances could stand for one another's outputs across two previous-frame reads, which would need an output
    /// older than the two each instance records.</summary>
    StandingChain = 10,
}
/// <summary>A refused set of graphs.</summary>
/// <param name="Code">Why it was refused.</param>
/// <param name="Message">The refusal, naming what it concerns.</param>
/// <param name="Names">The instances, passes, packages and versions concerned, in the message's order.</param>
public sealed record RenderGraphRuntimeRefusal(RenderGraphRuntimeRefusalCode Code, string Message, IReadOnlyList<string> Names);
/// <summary>Runs a set of render-graph instances frame by frame: schedules each frame with
/// <see cref="RenderGraphScheduler"/> into one of two schedules it alternates, then renders each scheduled instance in
/// render order through its own <see cref="ShaderPipelineRenderNode"/>, whose one submission records the graph's shader
/// passes and, through <see cref="RenderGraphPackageRecorders"/>, its package passes, all in the planner's order with
/// its barriers.
/// <para>
/// Before an instance renders, each of its inputs is bound to the frame of its producer's output the schedule names: this
/// frame's for a same-frame read of a producer that rendered first, otherwise the latest output completed before it, so
/// a consumer never waits for a slower producer and a previous-frame read takes history. An image input binds the
/// producer's published image; a buffer input binds the buffer the producer's default output holds in the frame slot
/// that produced it. An image input whose producer has no completed output of that frame (never rendered, still
/// building, or lost with the device) binds a transparent-black stand-in, so the consumer still renders on schedule; a
/// buffer has no stand-in, so an instance whose buffer producer has no completed output yet does not render.
/// </para>
/// <para>
/// An external instance (<see cref="RenderGraphInstanceKind.External"/>) has no graph: the
/// <see cref="IRenderGraphExternalProducer"/> registered for its package renders it through submissions of its own, at
/// the scheduled extent, before its consumers. Each consumer that renders binds the producer's latest completed output,
/// scheduled this frame or not, with a lease its node holds until the submission that sampled the image has finished.
/// An external instance whose package registers an upload instead (<see cref="RenderGraphPackageRecorders.RegisterSource"/>)
/// is an uploaded source: it renders through a node running the one-pass conversion graph its upload's descriptor names,
/// over the region the upload writes, and its consumers bind its output as a graph instance's.
/// </para>
/// <para>
/// Each instance counts its own work (<see cref="Work"/>). The root instance is what the display shows: the runtime's
/// output is its latest completed image. The root is a graph instance, or an external producer when the display shows
/// that producer's output with nothing drawn over it. A capture armed on the runtime reads the root, and one armed
/// through <see cref="CaptureTarget"/> reads the instance it names: a graph instance's node serves it on a frame the
/// instance renders with every image input bound to a completed output, never a stand-in, and an external producer
/// serves it on the next frame it produces. A steady frame, one whose schedule and extents repeat an earlier one,
/// allocates nothing.
/// </para>
/// <para>
/// No capture reads external content the capture gate did not fill. An external producer states whether an image it
/// hands out holds such content (<see cref="RenderGraphExternalOutput.Tainted"/>), and an instance whose latest render
/// bound a tainted image is tainted itself. A frame produced while a capture is pending is a capture frame: it renders
/// every tainted instance the captured instance reads, directly or through other instances, again whatever its refresh
/// and the budget (<see cref="RenderGraphFrame.Rerender"/>), before the instances that read it, and a capture moves to
/// its instance only on a frame whose every bound input is untainted.
/// </para>
/// </summary>
public sealed partial class RenderGraphRuntime : ICaptureRequestTarget, IDisposable, IRenderGraphInstances {
    private readonly CaptureRequestSlot m_capture = new();
    // Each instance's capture target by name, created when first asked for and kept across a reconfiguration, so a target
    // a caller holds keeps reading the instance of its name.
    private readonly Dictionary<string, InstanceCaptureTarget> m_captureTargets = new(comparer: StringComparer.Ordinal);

    private readonly IGpuDeviceContext m_device;
    // Every image the runtime's nodes create, leased to each reader the runtime binds it to (RenderGraphRuntime.Leases.cs).
    private readonly GpuImageLeases m_images;
    private readonly bool m_hostsOnDirectX;
    private readonly uint m_inFlightFrames;
    private readonly RenderGraphPackageRecorders m_packages;
    private readonly GpuPassPipelineCache m_pipelines;

    private Output[] m_current;
    // Each instance's graph, or null for an external instance and for a graph instance whose graph is not installed yet.
    private RenderGraphRuntimeGraph?[] m_graphs;
    private Binding[][] m_inputs;
    // Each instance's node, or null for an external instance, which has its producer instead.
    private ShaderPipelineRenderNode?[] m_nodes;
    private Output[] m_previous;
    private IRenderGraphExternalProducer?[] m_producers;
    private int m_root;
    private RenderGraphSchedule[] m_schedules;
    private RenderGraphInstanceSet m_set;
    // Each graph instance's producer whose stand-in its latest render bound, or null when every image input it bound was a
    // completed output; a capture of the instance waits until it is null.
    private string?[] m_standInReads;
    // Each instance's producer whose tainted output its latest render bound (a graph instance's inputs, an external
    // producer's reads), or null when everything it bound was untainted; a capture of the instance waits until it is null.
    private string?[] m_taintedReads;
    // Each instance's unread frames: the frames whose schedule left it unread and no displayed output shows or reads it,
    // including through held consumer outputs. Its packages read it, so a parked instance shown again
    // starts what depends on continuity anew.
    private long[] m_unreadFrames;
    // The instance the capture armed on the runtime reads.
    private int m_captureInstance;
    private bool m_captureFollowsRoot;
    private bool m_disposed;
    private RenderGraphHistory m_history;
    private RenderGraphSchedule? m_latest;
    private int m_turn;
    private int m_unproduced;

    private RenderGraphRuntime(RenderGraphInstanceSet set, RenderGraphRuntimeGraph?[] graphs, ShaderPipelineRenderNode?[] nodes, IRenderGraphExternalProducer?[] producers, SourceGraph?[] sources, Binding[][] inputs, int root, IGpuDeviceContext device, RenderGraphPackageRecorders packages, GpuPassPipelineCache pipelines, bool hostsOnDirectX, uint inFlightFrames, GpuImageLeases images) {
        m_images = images;
        m_current = new Output[nodes.Length];
        m_device = device;
        m_graphs = graphs;
        m_hostsOnDirectX = hostsOnDirectX;
        m_inFlightFrames = inFlightFrames;
        m_packages = packages;
        m_pipelines = pipelines;
        m_history = RenderGraphHistory.Empty(set: set);
        m_inputs = inputs;
        m_nodes = nodes;
        m_previous = new Output[nodes.Length];
        m_producers = producers;
        m_root = root;
        m_sources = sources;
        m_schedules = [
            new RenderGraphSchedule(set: set),
            new RenderGraphSchedule(set: set),
        ];
        m_set = set;
        m_standInReads = new string?[nodes.Length];
        m_taintedReads = new string?[nodes.Length];
        ResetStale(count: nodes.Length);
        m_producerTainted = new bool[nodes.Length];
        m_unreadFrames = new long[nodes.Length];
        m_captureInstance = root;
        ResetOwedReadbacks();

        Array.Fill(
            array: m_current,
            value: Output.None
        );
        Array.Fill(
            array: m_previous,
            value: Output.None
        );
    }

    /// <summary>The frames in flight each instance's node keeps when <see cref="TryCreate"/> is given none: a lease a
    /// package pass samples is held until its frame slot's next fence wait, so this many frames' leases of one image
    /// source can be outstanding at once, the frame recording included.</summary>
    public const uint DefaultInFlightFrames = 3;

    /// <summary>Gets whether every instance the latest frame scheduled produced its output: false before the first
    /// frame, and while any scheduled instance's graph is still building.</summary>
    public bool IsSettled => ((m_latest is not null) && (m_unproduced == 0));
    /// <summary>Gets the instance set the runtime schedules.</summary>
    public RenderGraphInstanceSet Instances => m_set;
    /// <summary>Gets the latest frame's schedule, or <see langword="null"/> before the first frame.</summary>
    public RenderGraphSchedule? Latest => m_latest;
    /// <inheritdoc/>
    /// <remarks>The runtime holds one capture at a time, whichever instance it reads: a path armed here, or forwarded to
    /// an instance's node or external producer and not yet served.</remarks>
    public string? PendingCapturePath {
        get {
            if (m_capture.PendingPath is { } armed) {
                return armed;
            }

            for (var index = 0; (index < m_nodes.Length); index++) {
                if ((((ICaptureRequestTarget?)m_nodes[index]) ?? m_producers[index])?.PendingCapturePath is { } forwarded) {
                    return forwarded;
                }
            }

            return null;
        }
    }
    /// <summary>Gets the root instance's name: the instance the display shows and captures read by default.</summary>
    public string Root => m_set.Instances[m_root].Name;
    /// <summary>Gets why a capture of the root would not be served by the frame the runtime produces now (see
    /// <see cref="UnservedCaptureReasonOf"/>), or <see langword="null"/> once the root has a completed output rendered
    /// from completed inputs.</summary>
    public string? UnservedCaptureReason => ReasonOf(index: m_root);
    /// <summary>Gets whether every instance whose node has submitted a frame has one completed on the GPU
    /// (<see cref="ShaderPipelineRenderNode.HasCompletedSubmission"/>), so a reader of GPU results waits on no instance's
    /// first frame. An instance that has not rendered and an external producer, which submits through its own device
    /// work, never hold it. Reading it allocates nothing.</summary>
    public bool FirstFramesCompleted => (FirstInFlight() < 0);
    /// <summary>Gets why <see cref="FirstFramesCompleted"/> is <see langword="false"/>, naming the first instance, in set
    /// order, whose first submission is still in flight, or <see langword="null"/> when it is
    /// <see langword="true"/>.</summary>
    public string? InFlightReason => ((FirstInFlight() is var index and >= 0)
        ? $"the instance '{m_set.Instances[index].Name}' has no GPU-completed frame: its first submission is still in flight"
        : null);

    // The first instance whose node has submitted and has no submission completed on the GPU, or -1.
    private int FirstInFlight() {
        for (var index = 0; (index < m_nodes.Length); index++) {
            if (
                (m_nodes[index] is { LatestSubmission: not null } node) &&
                !node.HasCompletedSubmission
            ) {
                return index;
            }
        }

        return -1;
    }
    private static RenderGraphRuntimeRefusal Refuse(RenderGraphRuntimeRefusalCode code, string message, params string[] names) => new(
        Code: code,
        Message: message,
        Names: names
    );
    // Validates one instance's graph against its instance and resolves its inputs to producer indices. A host buffer port
    // (an uploaded source's region) is the host's to bind, never an instance's output.
    private static bool TryResolve(RenderGraphInstanceSet set, int index, RenderGraphRuntimeGraph graph, RenderGraphPackageRecorders packages, Published?[] published, [NotNullWhen(returnValue: true)] out Binding[]? bindings, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        var instance = set.Instances[index];
        var plan = graph.Pipeline.Plan;

        bindings = null;

        if (packages.TryFindUnserved(
            pass: out var unserved,
            plan: plan
        )) {
            refusal = Refuse(
                RenderGraphRuntimeRefusalCode.PackageUnserved,
                RenderGraphPackageRecorders.Unserved(
                    instance: instance.Name,
                    package: unserved.Package!.Package,
                    pass: unserved.Name
                ),
                instance.Name,
                unserved.Name,
                unserved.Package!.Package
            );

            return false;
        }

        var output = plan.FindResource(name: plan.DefaultOutput);

        if (
            (output is null) ||
            (plan.Storages[output.Storage].Declaration.Kind != instance.Output)
        ) {
            refusal = Refuse(
                RenderGraphRuntimeRefusalCode.OutputKind,
                $"Instance '{instance.Name}' declares a {instance.Output} output, but its graph '{plan.Definition.Name}' publishes '{plan.DefaultOutput}' as {(output?.Declaration.Kind.ToString() ?? "nothing")}.",
                instance.Name,
                plan.DefaultOutput
            );

            return false;
        }

        var external = plan.Storages.Where(predicate: static storage => (storage.IsExternal && !storage.Declaration.IsHostBuffer)).ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static storage => storage.Name
        );
        var inputs = (graph.Inputs ?? []);
        var resolved = new Binding[inputs.Count];
        var bound = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var position = 0; (position < inputs.Count); position++) {
            var input = inputs[position];

            if (
                !external.TryGetValue(
                    key: (input.Version ?? string.Empty),
                    value: out var storage
                ) ||
                !bound.Add(item: input.Version!)
            ) {
                refusal = Refuse(
                    RenderGraphRuntimeRefusalCode.InputVersion,
                    $"Instance '{instance.Name}' binds '{input.Version}', which is not an unbound external version of its graph '{plan.Definition.Name}'.",
                    instance.Name,
                    (input.Version ?? string.Empty)
                );

                return false;
            }

            var producer = set.IndexOf(name: (input.Producer ?? string.Empty));
            var edge = -1;

            for (var read = 0; (read < set.Reads[index].Count); read++) {
                if (set.Reads[index][read].Producer == producer) {
                    edge = read;
                }
            }

            if (
                (producer < 0) ||
                (edge < 0)
            ) {
                refusal = Refuse(
                    RenderGraphRuntimeRefusalCode.InputProducer,
                    $"Instance '{instance.Name}' binds '{input.Version}' to '{input.Producer}', which it does not read.",
                    instance.Name,
                    input.Version!,
                    (input.Producer ?? string.Empty)
                );

                return false;
            }
            if (storage.Declaration.Kind != set.Instances[producer].Output) {
                refusal = Refuse(
                    RenderGraphRuntimeRefusalCode.InputKind,
                    $"Instance '{instance.Name}' binds {storage.Declaration.Kind} '{input.Version}' to '{input.Producer}', whose output is {set.Instances[producer].Output}.",
                    instance.Name,
                    input.Version!,
                    input.Producer!
                );

                return false;
            }
            if (Mismatch(
                declaration: storage.Declaration,
                published: published[producer]
            ) is { } mismatch) {
                refusal = Refuse(
                    RenderGraphRuntimeRefusalCode.InputSize,
                    $"Instance '{instance.Name}' binds '{input.Version}' as {mismatch.Declared}, but '{input.Producer}' publishes {mismatch.Published}.",
                    instance.Name,
                    input.Version!,
                    input.Producer!
                );

                return false;
            }

            resolved[position] = new Binding(
                Format: ((storage.Declaration.Kind == ShaderPipelineResourceKind.Image)
                    ? ShaderPipelineRenderNode.ParseFormat(format: storage.Declaration.Format)
                    : default),
                Kind: storage.Declaration.Kind,
                PreviousFrame: set.Reads[index][edge].PreviousFrame,
                Producer: producer,
                ProducerName: set.Instances[producer].Name,
                Version: input.Version!
            );
        }

        var unbound = external.Keys.FirstOrDefault(predicate: name => !bound.Contains(item: name));

        // Only an uploaded source's upload binds a host buffer port; any other instance would leave it unbound.
        if (
            (unbound is null) &&
            (instance.Kind != RenderGraphInstanceKind.External)
        ) {
            unbound = plan.Storages.FirstOrDefault(predicate: static storage => storage.Declaration.IsHostBuffer)?.Name;
        }
        if (unbound is not null) {
            refusal = Refuse(
                RenderGraphRuntimeRefusalCode.InputVersion,
                $"Instance '{instance.Name}' leaves the external version '{unbound}' of its graph '{plan.Definition.Name}' unbound.",
                instance.Name,
                unbound
            );

            return false;
        }

        bindings = resolved;
        refusal = null;

        return true;
    }

    /// <summary>Installs a set's graphs: one node per rendered instance, each holding its graph as a candidate that
    /// builds when the instance first renders, and one external producer per external instance, created by the factory
    /// its package registers.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="graphs">Each instance's graph, parallel to <see cref="RenderGraphInstanceSet.Instances"/>, and
    /// <see langword="null"/> for an external instance and for a graph instance whose graph is not compiled yet, which
    /// renders nothing until <see cref="TryInstall"/> gives it one; its consumers bind a stand-in meanwhile.</param>
    /// <param name="root">The name of the instance the display shows and captures read, which renders a graph.</param>
    /// <param name="packages">The recorders the graphs' package passes run through, and the external producers.</param>
    /// <param name="pipelines">The pass pipelines every instance's node leases its pipelines from.</param>
    /// <param name="deviceContext">The device every instance records on.</param>
    /// <param name="hostsOnDirectX">Whether the device is Direct3D 12.</param>
    /// <param name="runtime">The runtime, when this returns <see langword="true"/>. The caller owns it.</param>
    /// <param name="refusal">Why the graphs were refused, when this returns <see langword="false"/>; every external
    /// producer created to learn its format was disposed then, and nothing else was created.</param>
    /// <param name="inFlightFrames">Each instance's frames in flight, at least two, since an instance's history and its
    /// previous-frame reads live in its previous frame slot.</param>
    /// <returns><see langword="true"/> when the graphs installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="graphs"/>,
    /// <paramref name="root"/>, <paramref name="packages"/>, <paramref name="pipelines"/> or
    /// <paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlightFrames"/> is less than two.</exception>
    /// <exception cref="InvalidDataException">A graph cannot be installed on a node: its shader compilation failed, or
    /// its plan is not one a node runs.</exception>
    public static bool TryCreate(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, string root, RenderGraphPackageRecorders packages, GpuPassPipelineCache pipelines, IGpuDeviceContext deviceContext, bool hostsOnDirectX, [NotNullWhen(returnValue: true)] out RenderGraphRuntime? runtime, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal, uint inFlightFrames = DefaultInFlightFrames) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: graphs);
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentNullException.ThrowIfNull(argument: pipelines);
        ArgumentNullException.ThrowIfNull(argument: deviceContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 2U,
            value: inFlightFrames
        );

        runtime = null;

        if (graphs.Count != set.Instances.Count) {
            refusal = Refuse(
                code: RenderGraphRuntimeRefusalCode.GraphCount,
                message: $"The runtime was given {graphs.Count} graphs for {set.Instances.Count} instances."
            );

            return false;
        }

        var rootIndex = set.IndexOf(name: root);

        if (
            (rootIndex < 0) ||
            (set.Instances[rootIndex].Output != ShaderPipelineResourceKind.Image)
        ) {
            refusal = Refuse(
                RenderGraphRuntimeRefusalCode.Root,
                $"The root '{root}' names no instance whose output is an image.",
                root
            );

            return false;
        }

        if (RefuseExternal(
            graphs: graphs,
            packages: packages,
            set: set
        ) is { } external) {
            refusal = external;

            return false;
        }

        var producers = new IRenderGraphExternalProducer?[graphs.Count];
        var nodes = new ShaderPipelineRenderNode?[graphs.Count];
        var sources = new SourceGraph?[graphs.Count];
        var installed = new RenderGraphRuntimeGraph?[graphs.Count];
        var images = new GpuImageLeases();

        try {
            for (var index = 0; (index < graphs.Count); index++) {
                var instance = set.Instances[index];

                if (instance.Kind != RenderGraphInstanceKind.External) {
                    continue;
                }

                if (RunsPackage(
                    instance: instance,
                    packages: packages
                )) {
                    installed[index] = PackageGraphOf(
                        fault: out _,
                        package: instance.ExternalPackage!
                    );
                } else if (packages.ServesSource(package: instance.ExternalPackage!)) {
                    (sources[index], nodes[index]) = CreateSource(
                        deviceContext: deviceContext,
                        hostsOnDirectX: hostsOnDirectX,
                        images: images,
                        inFlightFrames: inFlightFrames,
                        instance: instance,
                        packages: packages,
                        pipelines: pipelines
                    );
                    installed[index] = sources[index]!.Graph;
                } else {
                    producers[index] = CreateProducer(
                        deviceContext: deviceContext,
                        hostsOnDirectX: hostsOnDirectX,
                        instance: instance,
                        packages: packages
                    );
                }
            }

            if (!TryBindAll(
                graphs: [.. graphs.Select(selector: (graph, index) => (installed[index] ?? graph))],
                inputs: out var inputs,
                packages: packages,
                producers: producers,
                refusal: out refusal,
                set: set
            )) {
                DisposeAll(
                    nodes: nodes,
                    producers: producers
                );
                DisposeSources(sources: sources);

                return false;
            }

            for (var index = 0; (index < graphs.Count); index++) {
                sources[index]?.Install(node: nodes[index]!);

                if (
                    (set.Instances[index].Kind != RenderGraphInstanceKind.Graph) &&
                    !RunsPackage(
                        instance: set.Instances[index],
                        packages: packages
                    )
                ) {
                    continue;
                }

                nodes[index] = CreateNode(
                    deviceContext: deviceContext,
                    hostsOnDirectX: hostsOnDirectX,
                    images: images,
                    inFlightFrames: inFlightFrames,
                    name: set.Instances[index].Name,
                    packages: packages,
                    pipelines: pipelines
                );

                if ((installed[index] ?? graphs[index]) is { } graph) {
                    nodes[index]!.Swap(pipeline: graph.Pipeline);
                    installed[index] = graph;
                }
            }

            refusal = null;
            runtime = new RenderGraphRuntime(
                device: deviceContext,
                graphs: installed,
                hostsOnDirectX: hostsOnDirectX,
                images: images,
                inFlightFrames: inFlightFrames,
                inputs: inputs,
                nodes: nodes,
                packages: packages,
                pipelines: pipelines,
                producers: producers,
                root: rootIndex,
                set: set,
                sources: sources
            );

            return true;
        } catch {
            DisposeAll(
                nodes: nodes,
                producers: producers
            );
            DisposeSources(sources: sources);

            throw;
        }
    }

    // Why an external instance cannot run as given, or null when every one can: it was given a graph, declares an output
    // that is not an image, or names a package neither an external producer nor an upload serves, and that no recorder
    // runs as an instance.
    private static RenderGraphRuntimeRefusal? RefuseExternal(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, RenderGraphPackageRecorders packages) {
        for (var index = 0; (index < graphs.Count); index++) {
            var instance = set.Instances[index];

            if (instance.Kind == RenderGraphInstanceKind.Graph) {
                continue;
            }

            var reason = ((graphs[index] is not null)
                ? "is given a graph, but its producer renders it"
                : ((instance.Output != ShaderPipelineResourceKind.Image)
                    ? $"declares a {instance.Output} output, but an external producer hands out images"
                    : ((packages.ServesProducer(package: instance.ExternalPackage!) || packages.ServesSource(package: instance.ExternalPackage!))
                        ? null
                        : PackageRefusal(
                            package: instance.ExternalPackage!,
                            packages: packages
                        ))));

            if (reason is not null) {
                return Refuse(
                    RenderGraphRuntimeRefusalCode.ExternalProducer,
                    $"External instance '{instance.Name}' of package '{instance.ExternalPackage}' {reason}.",
                    instance.Name,
                    instance.ExternalPackage!
                );
            }
        }

        return null;
    }
    // Resolves every installed graph's inputs against what each producer publishes.
    private static bool TryBindAll(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, IRenderGraphExternalProducer?[] producers, RenderGraphPackageRecorders packages, [NotNullWhen(returnValue: true)] out Binding[][]? inputs, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        var published = new Published?[graphs.Count];

        for (var index = 0; (index < graphs.Count); index++) {
            published[index] = PublishedBy(
                graph: graphs[index],
                producer: producers[index]
            );
        }

        inputs = new Binding[graphs.Count][];

        for (var index = 0; (index < graphs.Count); index++) {
            if (graphs[index] is not { } graph) {
                inputs[index] = [];

                continue;
            }
            if (!TryResolve(
                bindings: out var bindings,
                graph: graph,
                index: index,
                packages: packages,
                published: published,
                refusal: out refusal,
                set: set
            )) {
                inputs = null;

                return false;
            }

            inputs[index] = bindings;
        }

        refusal = RefuseStandingChains(
            graphs: graphs,
            inputs: inputs,
            producers: producers,
            set: set
        );

        if (refusal is not null) {
            inputs = null;

            return false;
        }

        return true;
    }
    private static IRenderGraphExternalProducer CreateProducer(RenderGraphInstance instance, RenderGraphPackageRecorders packages, IGpuDeviceContext deviceContext, bool hostsOnDirectX) => packages.CreateProducer(context: new RenderGraphExternalProducerContext(
        Device: deviceContext,
        HostsOnDirectX: hostsOnDirectX,
        Instance: instance.Name,
        Package: instance.ExternalPackage!,
        Settings: instance.Settings
    ));
    // The extent is a placeholder: the instance's first render requests its scheduled extent before the node builds
    // anything.
    private static ShaderPipelineRenderNode CreateNode(string name, RenderGraphPackageRecorders packages, GpuPassPipelineCache pipelines, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint inFlightFrames, GpuImageLeases images) => new(
        deviceContext: deviceContext,
        height: 1,
        hostsOnDirectX: hostsOnDirectX,
        images: images,
        inFlightFrames: inFlightFrames,
        name: name,
        outputLayout: GpuImageLayout.ShaderReadOnly,
        packages: packages,
        pipelines: pipelines,
        width: 1
    );
    // What an instance publishes to its consumers: its graph's default output as its node presents it, or its external
    // producer's images; nothing known for a graph instance whose graph is not installed yet.
    private static Published? PublishedBy(RenderGraphRuntimeGraph? graph, IRenderGraphExternalProducer? producer) {
        if (producer is not null) {
            return new Published(
                Format: producer.Format,
                SizeBytes: 0UL
            );
        }

        if (graph is null) {
            return null;
        }

        var plan = graph.Pipeline.Plan;

        if (plan.FindResource(name: plan.DefaultOutput) is not { } output) {
            return default(Published);
        }

        var declaration = plan.Storages[output.Storage].Declaration;

        return ((declaration.Kind == ShaderPipelineResourceKind.Buffer)
            ? new Published(
                Format: default,
                SizeBytes: declaration.SizeBytes.GetValueOrDefault()
            )
            : new Published(
                Format: ShaderPipelineRenderNode.PublishedFormat(output: declaration),
                SizeBytes: 0UL
            ));
    }
    // Why a consumer's external version cannot bind what its producer publishes, or null when it can: a buffer larger than
    // the producer's. An external image is only ever sampled, so it binds an image of any format its producer publishes,
    // a float working image or an RGBA8 one alike. A producer that publishes nothing known yet, a graph instance whose
    // graph is not installed, is checked when its graph installs.
    private static (string Declared, string Published)? Mismatch(ShaderPipelineResource declaration, Published? published) {
        if (
            (published is not { } known) ||
            (declaration.Kind != ShaderPipelineResourceKind.Buffer)
        ) {
            return null;
        }

        var size = declaration.SizeBytes.GetValueOrDefault();

        return ((size > known.SizeBytes)
            ? ($"a {size}-byte buffer", $"a {known.SizeBytes}-byte buffer")
            : null);
    }
    private static void DisposeAll(ShaderPipelineRenderNode?[] nodes, IRenderGraphExternalProducer?[] producers) {
        foreach (var node in nodes) {
            node?.Dispose();
        }
        foreach (var producer in producers) {
            producer?.Dispose();
        }
    }
    // Binds each of an instance's inputs to the frame of its producer's output the schedule names, or an image input to a
    // stand-in when the producer has no completed output of that frame or earlier. An external producer's input binds
    // its latest completed output under a lease the node holds for the submission that samples it. A buffer has no
    // stand-in: an instance whose buffer producer has no completed output does not render, and false says so before
    // any image is bound or leased.
    private bool Bind(int index, RenderGraphSchedule schedule) {
        var bindings = m_inputs[index];

        m_standInReads[index] = null;
        m_taintedReads[index] = null;

        if (bindings.Length == 0) {
            return true;
        }

        var node = m_nodes[index]!;
        var consumer = m_set.Instances[index].Name;

        foreach (var binding in bindings) {
            if (binding.Kind != ShaderPipelineResourceKind.Buffer) {
                continue;
            }

            var buffered = OutputAt(
                frame: FrameOf(
                    consumer: consumer,
                    producer: binding.ProducerName,
                    schedule: schedule
                ),
                producer: binding.Producer,
                readFrame: (schedule.Frame - (binding.PreviousFrame ? 1L : 0L))
            );

            if (buffered.Buffer is not { } buffer) {
                return false;
            }

            NoteTaint(
                index: index,
                producer: binding.ProducerName,
                tainted: buffered.Tainted
            );
            node.BindBuffer(
                buffer: buffer,
                name: binding.Version
            );
        }
        foreach (var binding in bindings) {
            if (binding.Kind == ShaderPipelineResourceKind.Buffer) {
                continue;
            }
            if (m_producers[binding.Producer] is { } producer) {
                var acquired = producer.TryAcquireOutput(output: out var external);

                if (acquired) {
                    m_producerTainted[binding.Producer] = external.Tainted;

                    if (Withholds(
                        previousFrame: binding.PreviousFrame,
                        tainted: external.Tainted
                    )) {
                        external.Lease.Retire();
                        node.BindImage(
                            image: StandInFor(format: binding.Format),
                            name: binding.Version
                        );

                        continue;
                    }
                }

                // A source whose image arrives from another thread or device as an image view alone hands out no image a
                // graph's barriers can name, so its reader draws a stand-in, as one of a producer with no output does.
                if (
                    acquired &&
                    !external.Image.IsSameDeviceImage
                ) {
                    external.Lease.Retire();
                    acquired = false;
                }

                if (acquired) {
                    NoteTaint(
                        index: index,
                        producer: binding.ProducerName,
                        tainted: external.Tainted
                    );
                    node.BindImage(
                        image: new ShaderPipelineExternalImage(
                            Format: external.Image.Format,
                            Height: external.Image.Height,
                            ImageHandle: external.Image.ImageHandle,
                            ImageViewHandle: external.Image.ImageViewHandle,
                            Layout: external.Layout,
                            Width: external.Image.Width
                        ),
                        lease: external.Lease,
                        name: binding.Version
                    );
                } else {
                    node.BindImage(
                        image: StandInFor(format: binding.Format),
                        name: binding.Version
                    );
                    NoteStandIn(
                        frame: FrameOf(
                            consumer: consumer,
                            producer: binding.ProducerName,
                            schedule: schedule
                        ),
                        index: index,
                        producer: binding.ProducerName
                    );
                }

                continue;
            }

            var frame = FrameOf(
                consumer: consumer,
                producer: binding.ProducerName,
                schedule: schedule
            );
            var output = OutputAt(
                frame: frame,
                producer: binding.Producer,
                readFrame: (schedule.Frame - (binding.PreviousFrame ? 1L : 0L))
            );

            if (
                output.Image.IsSameDeviceImage &&
                Withholds(
                    previousFrame: binding.PreviousFrame,
                    tainted: output.Tainted
                )
            ) {
                node.BindImage(
                    image: StandInFor(format: binding.Format),
                    name: binding.Version
                );
            } else if (output.Image.IsSameDeviceImage) {
                NoteTaint(
                    index: index,
                    producer: binding.ProducerName,
                    tainted: output.Tainted
                );
                node.BindImage(
                    image: new ShaderPipelineExternalImage(
                        Format: output.Image.Format,
                        Height: output.Image.Height,
                        ImageHandle: output.Image.ImageHandle,
                        ImageViewHandle: output.Image.ImageViewHandle,
                        Layout: output.Layout,
                        Width: output.Image.Width
                    ),
                    lease: LeaseOf(image: output.Image),
                    name: binding.Version
                );
            } else {
                node.BindImage(
                    image: StandInFor(format: binding.Format),
                    name: binding.Version
                );
                NoteStandIn(
                    frame: frame,
                    index: index,
                    producer: binding.ProducerName
                );
            }
        }

        return true;
    }
    // Records a stand-in bound for a read the schedule shows this frame, which a capture of the consumer waits out. A
    // read the frame does not show (no footprint, or the producer off view) samples nothing a capture would see.
    private void NoteStandIn(int index, string producer, long frame) {
        if (frame >= 0) {
            m_standInReads[index] = producer;
        }
    }
    // The frame of a producer's output the schedule has a consumer read, or -1 when it names none.
    private static long FrameOf(RenderGraphSchedule schedule, string consumer, string producer) {
        var reads = schedule.Reads;

        for (var read = 0; (read < reads.Count); read++) {
            var row = reads[read];

            if (
                ReferenceEquals(
                    objA: row.Consumer,
                    objB: consumer
                ) &&
                ReferenceEquals(
                    objA: row.Producer,
                    objB: producer
                )
            ) {
                return row.Frame;
            }
        }

        return -1L;
    }
    // Selects the producer's recorded output at the scheduled frame, then resolves what it stands for at the reader's
    // frame. A slow standing output follows its owner even when its own last render is older than the owner's history.
    private Output OutputAt(int producer, long frame, long readFrame) => Resolve(
        frame: readFrame,
        output: RecordedAt(
            frame: frame,
            producer: producer
        )
    );
    private void Release() {
        RetireShownLeases();
        Array.Fill(
            array: m_current,
            value: Output.None
        );
        Array.Fill(
            array: m_previous,
            value: Output.None
        );
        Array.Clear(array: m_standInReads);
        Array.Clear(array: m_taintedReads);
        ResetStale(count: m_stale.Length);
        Array.Clear(array: m_producerTainted);
        m_history = RenderGraphHistory.Empty(set: m_set);
        m_latest = null;
        m_readFrame = -1;
        m_unproduced = 0;
    }
    // Why a capture of an instance would not be served by the frame the runtime produces now, or null when it would.
    private string? ReasonOf(int index) {
        var name = m_set.Instances[index].Name;

        if (m_producers[index] is { } producer) {
            return ((producer.NotReadyReason is { } reason)
                ? $"the instance '{name}' has produced no output: {reason}"
                : TaintReasonOf(
                    index: index,
                    name: name
                ));
        }
        if (m_sources[index]?.Fault is { } fault) {
            return $"the instance '{name}' has produced no output: {fault}";
        }
        if (m_current[index].StandsFor.IsRetired) {
            return $"the instance '{name}' published the image of an instance that retired, and has not rendered since";
        }
        if (
            !m_nodes[index]!.IsReady ||
            (LatestOf(index: index).Frame < 0)
        ) {
            return $"the instance '{name}' has produced no output";
        }

        var (width, height) = m_nodes[index]!.Extent;
        var (requestedWidth, requestedHeight) = m_nodes[index]!.RequestedExtent;

        if ((width != requestedWidth) || (height != requestedHeight)) {
            return $"the instance '{name}' renders at {width}x{height} while {requestedWidth}x{requestedHeight} is requested";
        }

        if (m_standInReads[index] is { } producerName) {
            return $"the instance '{name}' has rendered only over a stand-in for '{producerName}', which has produced no output";
        }

        return (TaintReasonOf(
            index: index,
            name: name
        ) ?? m_stale[index]);
    }
    // Arms a capture of one instance on the runtime's one slot.
    private void Arm(int index, FrameCaptureRequest request, bool followsRoot = false) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        if ((request.Converge > 0) && !CanConverge(index: index)) {
            throw new InvalidOperationException(message: "A convergence capture requires a rendered graph instance.");
        }
        m_capture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
        m_captureInstance = index;
        m_captureFollowsRoot = followsRoot;
        BeginConvergence(captured: index, request: request);
    }

    /// <inheritdoc/>
    /// <remarks>Fails a capture still armed on the runtime, then disposes every instance's node, each waiting out its
    /// own submissions and failing a capture it holds.</remarks>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: nameof(RenderGraphRuntime)));
        // The nodes first: each waits out its submissions and retires the leases they sampled, so every producer's
        // output is released before the producer is.
        DisposeAll(
            nodes: m_nodes,
            producers: m_producers
        );
        DisposeSources(sources: m_sources);
        RetireShownLeases();
        ReleaseStandIns(wait: true);
    }
    /// <summary>Releases every instance's device objects after the device was lost and recreated, so nothing of the old
    /// device survives: each node releases its graph and package recorders, the stand-ins are released, and every
    /// instance starts again from no completed output and no scheduling history, so the next frame renders every
    /// instance something shows and rebuilds it on the new device. A capture still armed on the runtime is refused
    /// (<see cref="CaptureRequestSlot.RefuseForDeviceLoss"/>), as the root's node refuses one forwarded to it.</summary>
    public void OnDeviceLost() {
        m_capture.RefuseForDeviceLoss();
        // Renders in flight on the lost device never complete, so a retired node owes nothing more.
        m_retiredOwing.Clear();
        m_releasingLostDevice = true;

        try {
            // A retired producer a node still holds hears of the loss when that node's loss releases it.
            foreach (var retired in m_retiredProducers) {
                retired.OnDeviceLost();
            }
            // The nodes first, retiring every lease their lost submissions held, then the producers.
            foreach (var node in m_nodes) {
                node?.OnDeviceLost();
            }
            foreach (var producer in m_producers) {
                producer?.OnDeviceLost();
            }
            foreach (var source in m_sources) {
                source?.OnDeviceLost();
            }

            PackagesLostDevice();
            ReleaseStandIns(wait: false);
            Release();
        } finally {
            m_releasingLostDevice = false;
        }
    }
    /// <summary>Schedules and renders one frame.</summary>
    /// <param name="frame">What the frame shows; its roots must include the root instance for it to render.</param>
    /// <param name="context">The host's frame context, handed to every rendering instance.</param>
    /// <returns>The root instance's latest completed image, or an empty surface before it has produced one.</returns>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    /// <exception cref="ArgumentException">The scheduler refused the frame (see
    /// <see cref="RenderGraphScheduler.Schedule"/>); nothing rendered, and the next frame is scheduled against the same
    /// history.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A root or footprint fraction, or the budget, is out of
    /// range.</exception>
    public Surface ProduceFrame(in RenderGraphFrame frame, in FrameContext context) {
        try {
            return ProduceFrameCore(frame: in frame, context: ConvergenceContext(context: in context));
        } catch {
            // A binding or producer may throw before its normal retirement point. Taken reads still belong to their
            // submitters; every untaken acquisition must retire before recovery can release the device's images.
            foreach (var reads in m_externalReads) {
                reads?.RetireUntaken();
            }

            throw;
        } finally {
            FinishHistory();
        }
    }

    private Surface ProduceFrameCore(in RenderGraphFrame frame, in FrameContext context) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        RebuildDriftedSources();
        PollOwedReadbacks();
        PackagesBeginFrame(context: in context);

        var schedule = m_schedules[m_turn];
        var prior = m_history;
        var sourced = WithSourceStates(frame: in frame);
        var rerendered = WithRerenders(frame: in sourced);
        var scheduled = WithUnchanged(
            context: in context,
            frame: in rerendered
        );

        RenderGraphScheduler.Schedule(
            frame: scheduled with { Costs = this },
            history: prior,
            schedule: schedule,
            set: m_set
        );
        m_turn ^= 1;
        m_history = schedule.Next;
        m_historyPrior = prior;
        m_historySchedule = schedule;
        Array.Clear(array: m_historySucceeded);
        m_latest = schedule;
        m_readFrame = frame.Index;
        m_unproduced = 0;
        ReleaseUnnamed(schedule: schedule);
        RescheduleAfterRelease(
            frame: scheduled with { Costs = this },
            prior: prior,
            schedule: schedule
        );
        CountUnread(schedule: schedule);

        // A source with no conversion graph is never scheduled. Its opening or descriptor fault still refuses any
        // same-frame consumer that shows it, rather than certifying a stand-in as a completed frame.
        for (var index = 0; (index < m_sources.Length); index++) {
            if (m_sources[index] is { Graph: null }) {
                MarkUnproduced(index: index, node: m_nodes[index]!);
            } else if (m_set.Instances[index].IsSource &&
                (m_producers[index] is IRenderGraphSourceProducer producer) &&
                (schedule.Instances[index] is { } row) &&
                ((row.Status == RenderGraphInstanceStatus.Refused) ||
                    ((row.Status == RenderGraphInstanceStatus.Waiting) && ((row.Width == 0) || (row.Height == 0))))) {
                // An unopened source or an unprovisioned probe has no extent to schedule, but it still answers:
                // a refusal must reach its consumers, and a filled source can already show its 1x1 fill, including
                // offscreen where the scheduler cannot pace a rate source against a display.
                var production = producer.Answer;

                if (production.IsRendered) {
                    MarkCurrent(index: index);
                } else {
                    MarkProduction(index: index, production: production);
                }
            }
        }

        var renders = schedule.Renders;
        // The node that submitted last this frame, whose submission follows the host's presentation of the last frame.
        ShaderPipelineRenderNode? submitter = null;
        var holdingConvergence = ((m_convergence is { IsActive: true } convergence) &&
            (convergence.Samples >= convergence.Request.Converge) &&
            (m_nodes[m_captureInstance]?.PendingCapturePath == convergence.Request.Path));

        for (var position = 0; (position < renders.Count); position++) {
            var index = renders[position];
            var row = schedule.Instances[index];

            // The display encoder may still be building after the last requested sample. Keep the contributing
            // images alive until readback completes; another render would silently capture a later jitter sample.
            if (holdingConvergence && m_convergenceInstances.Contains(item: index)) {
                if (index == m_captureInstance) {
                    m_nodes[index]?.PollCapture();
                }
                m_unproduced++;
                schedule.Next.Withdraw(index: index, previous: prior);
                continue;
            }

            // An external producer submits through its own ring, at the scheduled extent, before its consumers render. A
            // capture of it moves to it first, and it serves the capture from the next frame it produces. A render it
            // could not produce is withdrawn from the history, so its cadence counts from its last completed frame and a
            // source that renders once is asked again on the next frame.
            if (m_producers[index] is { } producer) {
                var reads = BindExternalReads(
                    index: index,
                    schedule: schedule
                );

                if (
                    (index == m_captureInstance) &&
                    (m_capture.PendingPath is not null) &&
                    (m_taintedReads[index] is null) &&
                    !MarkReadStale(index: index, schedule: schedule)
                ) {
                    m_capture.Forward(target: producer);
                }

                var produced = (((row.Width > 0) && (row.Height > 0))
                    ? producer.Produce(
                        context: in context,
                        height: ((uint)row.Height),
                        reads: reads,
                        width: ((uint)row.Width)
                    )
                    : FrameRender.Waiting(reason: "the schedule gave it no extent"));

                reads?.RetireUntaken();

                if (!produced.IsRendered) {
                    m_unproduced++;
                    MarkProduction(
                        index: index,
                        production: produced
                    );
                    schedule.Next.Withdraw(
                        index: index,
                        previous: prior
                    );
                } else {
                    m_historySucceeded[index] = true;
                    MarkRendered(index: index, schedule: schedule);
                }

                continue;
            }

            var node = m_nodes[index]!;
            var source = m_sources[index];

            // An uploaded source writes its image for the frame's tick into its region; one with no image is withdrawn,
            // as an external producer's render is, so its cadence counts from its last converted frame.
            if (
                (source is not null) &&
                (source.Write(
                    node: node,
                    tick: frame.Tick
                ) is { IsRendered: false } written)
            ) {
                m_unproduced++;

                if (written.Completion == FrameCompletion.Refused) {
                    MarkProduction(
                        index: index,
                        production: written
                    );
                } else {
                    MarkUnproduced(
                        index: index,
                        node: node,
                        waiting: written.Reason
                    );
                }

                schedule.Next.Withdraw(
                    index: index,
                    previous: prior
                );

                continue;
            }
            if (!Bind(
                index: index,
                schedule: schedule
            )) {
                m_unproduced++;
                if (!MarkReadStale(index: index, schedule: schedule)) {
                    MarkStale(
                        index: index,
                        reason: $"the instance '{m_set.Instances[index].Name}' reads a buffer with no completed output"
                    );
                }

                continue;
            }

            // The reads its graph binds to no version reach its package passes, their taint the instance's; what they
            // leave is retired with the frame.
            node.Reads = BindUnboundReads(
                index: index,
                schedule: schedule
            );
            // A source's graph renders at the extent its descriptor fixed, which it declared to the scheduler.
            if (
                (source is null) &&
                (row.Width > 0) &&
                (row.Height > 0)
            ) {
                node.Resize(
                    height: ((uint)row.Height),
                    width: ((uint)row.Width)
                );
            }
            // A capture moves to its instance's node only once that node renders a graph over completed inputs, so the
            // frame it produces is the one the capture reads; until then it stays armed here, where
            // UnservedCaptureReasonOf explains it.
            // A node whose published image stands for one that is gone serves no capture until it has rendered again,
            // which a shown one does this frame (WithRerenders).
            if (
                (index == m_captureInstance) &&
                (m_capture.PendingPath is not null) &&
                CanServeConvergence &&
                node.IsReady &&
                (m_standInReads[index] is null) &&
                (m_taintedReads[index] is null) &&
                (m_current[index].StandsFor.IsOwn || (LatestOf(index: index).Frame >= 0)) &&
                !MarkReadStale(index: index, schedule: schedule)
            ) {
                m_capture.Forward(target: node);
            }

            OfferCaptureSource(
                index: index,
                node: node
            );

            var rendered = node.FrameCounter;
            var submitted = node.SubmissionCount;
            Surface surface;

            // The root is shown as the display, at its own extent; every other instance is resampled by what reads it.
            node.ShownAtItsExtent = (index == m_root);
            node.UnreadFrames = m_unreadFrames[index];

            try {
                surface = node.ProduceFrame(context: in context);
            } finally {
                if (node.FrameCounter != rendered) {
                    m_historySucceeded[index] = true;
                    RememberOutput(index: index, node: node, schedule: schedule, surface: node.PublishedSurface);
                }
                node.Reads?.RetireUntaken();
                node.Reads = null;
                NoteOwedReadbacks(index: index);
            }

            if (node.FrameCounter == rendered) {
                m_unproduced++;
                MarkUnproduced(
                    index: index,
                    node: node
                );
                // A paused node presents its last image as this frame's output on purpose, writing nothing: the frame it
                // was scheduled for is spent, so its refresh, and the demand it passes to its producers, keep their cadence.
                if (Stands(index: index)) {
                    m_historySucceeded[index] = true;
                }

                // A source whose conversion has not built yet is asked again, since its cadence may never ask twice.
                if (source is not null) {
                    schedule.Next.Withdraw(
                        index: index,
                        previous: prior
                    );
                }

                if (node.SubmissionCount == submitted) {
                    continue;
                }
            }

            // A sample rendered at an extent the node was not asked for is one its capture never reads.
            if ((node.FrameCounter != rendered) && (index == m_captureInstance) && IsConverging(index: index) &&
                (m_standInReads[index] is null) && (m_taintedReads[index] is null) && (node.Extent == node.RequestedExtent)) {
                m_convergence!.Count();
            }

            MarkRendered(
                index: index,
                schedule: schedule
            );

            submitter = node;
            if (node.FrameCounter == rendered) {
                RememberOutput(index: index, node: node, schedule: schedule, surface: in surface);
            }
        }

        ConvertForCapture(
            context: in context,
            frame: frame.Index,
            schedule: schedule,
            tick: frame.Tick
        );
        Complete();

        return Shown(
            image: RootImage(),
            submitter: submitter
        );
    }
    // The root's latest completed image: a graph root's output, resolved through what it stands for, or an external
    // root's latest output, whose acquisition is released at once, since the surface is valid only
    // until the next frame, the first time the producer can replace it.
    private Surface RootImage() {
        if (m_producers[m_root] is not { } producer) {
            return LatestOf(index: m_root).Image;
        }
        if (!producer.TryAcquireOutput(output: out var output)) {
            return default;
        }

        m_producerTainted[m_root] = output.Tainted;
        output.Lease.Retire();

        return output.Image;
    }

    /// <inheritdoc/>
    /// <remarks>The capture follows the next composed root across a reconfiguration. A named
    /// <see cref="CaptureTarget"/> continues to capture its named instance instead.</remarks>
    public void RequestCapture(FrameCaptureRequest request) => Arm(
        followsRoot: true,
        index: m_root,
        request: request
    );
    /// <summary>Returns the capture target of one instance. A capture armed on it is served by the first frame after it
    /// is armed on which a graph instance renders an installed graph, at the extent last requested of it, with every image
    /// input bound to a completed output,
    /// or on which an external instance produces. The runtime holds one capture at a time across its instances. A
    /// requester that stops waiting withdraws it with <see cref="FrameCaptureRequest.TryFail"/>, and the runtime then
    /// drops it.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The instance's target, the same object on every call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The set has no instance of that name.</exception>
    public ICaptureRequestTarget CaptureTarget(string instance) {
        _ = IndexOf(instance: instance);

        if (!m_captureTargets.TryGetValue(
            key: instance,
            value: out var target
        )) {
            target = new InstanceCaptureTarget(
                name: instance,
                runtime: this
            );
            m_captureTargets.Add(
                key: instance,
                value: target
            );
        }

        return target;
    }
    /// <summary>Returns why a capture of one instance would not be served by the frame the runtime produces now, phrased
    /// as the refusal of a capture that waited on it reads: the instance has no completed output, its installed graph
    /// renders at an extent other than the one last requested of it (a resize building, or refused), or its latest render
    /// bound a stand-in for a producer that has none. It builds a string, so a caller polls it only to report.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The reason, or <see langword="null"/> when a capture of it would be served.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The set has no instance of that name.</exception>
    public string? UnservedCaptureReasonOf(string instance) => ReasonOf(index: IndexOf(instance: instance));

    private int IndexOf(string instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var index = m_set.IndexOf(name: instance);

        return ((index >= 0)
            ? index
            : throw new ArgumentException(
                message: $"The render graph has no instance '{instance}'.",
                paramName: nameof(instance)
            ));
    }

    /// <summary>Returns the node an instance renders its graph through, for inspection.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentException">The instance renders through an external producer.</exception>
    public ShaderPipelineRenderNode Node(int instance) => (m_nodes[instance] ?? throw new ArgumentException(
        message: $"Instance '{m_set.Instances[instance].Name}' is an external producer, which renders through no node.",
        paramName: nameof(instance)
    ));
    /// <summary>Returns the external producer an external instance renders through, for inspection.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The producer, or <see langword="null"/> for an instance that renders a graph.</returns>
    public IRenderGraphExternalProducer? Producer(int instance) => m_producers[instance];
    /// <summary>Returns an instance's counted GPU work: its own submissions, per pass of its graph, shader and package
    /// passes alike, or of an external producer's submissions.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The instance's work source.</returns>
    public IGpuWorkSource Work(int instance) => (((IGpuWorkSource?)m_nodes[instance]) ?? m_producers[instance]!.Work);
    /// <summary>Returns the bytes of the host-written regions an instance's installed graph reads
    /// (<see cref="ShaderPipelineRenderNode.RegionBytes"/>), for inspection.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The bytes, or <see langword="null"/> for an instance that renders through an external producer.</returns>
    public ulong? RegionBytes(int instance) => m_nodes[instance]?.RegionBytes;
    /// <summary>Returns the installed graph regions' actual GPU memory and retained CPU payload storage.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The region account, or null for an external producer.</returns>
    public RenderGraphRegionMemory? RegionMemory(int instance) => m_nodes[instance]?.RegionMemory;

    // One instance's capture target: a capture armed on it arms the runtime's one slot for the instance of its name, and is
    // refused once a reconfiguration has removed that instance.
    private sealed class InstanceCaptureTarget(RenderGraphRuntime runtime, string name) : ICaptureRequestTarget {
        public string? PendingCapturePath => runtime.PendingCapturePath;

        public void RequestCapture(FrameCaptureRequest request) {
            ArgumentNullException.ThrowIfNull(argument: request);

            var index = runtime.m_set.IndexOf(name: name);

            if (index < 0) {
                _ = request.TryFail(error: new InvalidOperationException(message: $"The render graph no longer has an instance '{name}'."));

                return;
            }

            runtime.Arm(
                index: index,
                request: request
            );
        }
    }
    // What a producer instance publishes: an image's format, or a buffer's size in bytes.
    private readonly record struct Published(GpuPixelFormat Format, ulong SizeBytes);
    // One input resolved at install: the version it binds, the producer whose output it reads, and whether it reads that
    // output's previous frame.
    private readonly record struct Binding(string Version, int Producer, string ProducerName, ShaderPipelineResourceKind Kind, GpuPixelFormat Format, bool PreviousFrame);
    // One completed output of an instance: the frame it belongs to, its published image and the layout it is in, its
    // buffer when it is one, whether it was rendered from a tainted input, and what the image stands for when it is not
    // the instance's own (RenderGraphRuntime.Standing.cs).
    private readonly record struct Output(long Frame, Surface Image, GpuImageLayout Layout, IGpuBuffer? Buffer, bool Tainted, Standing StandsFor, ulong? StateTick = null) {
        public static Output None => new(
            Buffer: null,
            Frame: -1,
            Image: default,
            Layout: GpuImageLayout.Undefined,
            StandsFor: Standing.Own,
            Tainted: false
        );
    }
}
