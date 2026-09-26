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
    /// <summary>An input binds an image version of another format than its producer publishes, or a buffer version
    /// larger than its producer's buffer.</summary>
    InputFormat = 8,
    /// <summary>An external instance was given a graph, declares an output that is not an image, or names a package no
    /// external producer serves.</summary>
    ExternalProducer = 9,
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
/// </summary>
public sealed partial class RenderGraphRuntime : ICaptureRequestTarget, IDisposable, IRenderGraphInstances {
    private readonly CaptureRequestSlot m_capture = new();
    // Each instance's capture target by name, created when first asked for and kept across a reconfiguration, so a target
    // a caller holds keeps reading the instance of its name.
    private readonly Dictionary<string, InstanceCaptureTarget> m_captureTargets = new(comparer: StringComparer.Ordinal);

    private readonly IGpuDeviceContext m_device;
    private readonly bool m_hostsOnDirectX;
    private readonly uint m_inFlightFrames;
    private readonly RenderGraphPackageRecorders m_packages;

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
    // The instance the capture armed on the runtime reads.
    private int m_captureInstance;
    private bool m_disposed;
    private RenderGraphHistory m_history;
    private RenderGraphSchedule? m_latest;
    private int m_turn;
    private int m_unproduced;

    private RenderGraphRuntime(RenderGraphInstanceSet set, RenderGraphRuntimeGraph?[] graphs, ShaderPipelineRenderNode?[] nodes, IRenderGraphExternalProducer?[] producers, SourceGraph?[] sources, Binding[][] inputs, int root, IGpuDeviceContext device, RenderGraphPackageRecorders packages, bool hostsOnDirectX, uint inFlightFrames) {
        m_current = new Output[nodes.Length];
        m_device = device;
        m_graphs = graphs;
        m_hostsOnDirectX = hostsOnDirectX;
        m_inFlightFrames = inFlightFrames;
        m_packages = packages;
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
        m_captureInstance = root;

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

    private static RenderGraphRuntimeRefusal Refuse(RenderGraphRuntimeRefusalCode code, string message, params string[] names) => new(
        Code: code,
        Message: message,
        Names: names
    );
    private static GpuPixelFormat PixelFormatOf(SurfaceFormat format) => (format switch {
        SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
        _ => GpuPixelFormat.R8G8B8A8Unorm,
    });
    // Validates one instance's graph against its instance and resolves its inputs to producer indices.
    // An uploaded source's graph (hosted) binds its one external version, its region, to a host buffer port, not to an
    // instance's output.
    private static bool TryResolve(RenderGraphInstanceSet set, int index, RenderGraphRuntimeGraph graph, bool hosted, RenderGraphPackageRecorders packages, Published?[] published, [NotNullWhen(returnValue: true)] out Binding[]? bindings, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
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

        var external = plan.Storages.Where(predicate: static storage => storage.IsExternal).ToDictionary(
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
                    RenderGraphRuntimeRefusalCode.InputFormat,
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
                Producer: producer,
                ProducerName: set.Instances[producer].Name,
                Version: input.Version!
            );
        }

        if (
            !hosted &&
            (external.Keys.FirstOrDefault(predicate: name => !bound.Contains(item: name)) is { } unbound)
        ) {
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
    /// <param name="deviceContext">The device every instance records on.</param>
    /// <param name="hostsOnDirectX">Whether the device is Direct3D 12.</param>
    /// <param name="runtime">The runtime, when this returns <see langword="true"/>. The caller owns it.</param>
    /// <param name="refusal">Why the graphs were refused, when this returns <see langword="false"/>; every external
    /// producer created to learn its format was disposed then, and nothing else was created.</param>
    /// <param name="inFlightFrames">Each instance's frames in flight, at least two, since an instance's history and its
    /// previous-frame reads live in its previous frame slot.</param>
    /// <returns><see langword="true"/> when the graphs installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="graphs"/>,
    /// <paramref name="root"/>, <paramref name="packages"/> or <paramref name="deviceContext"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlightFrames"/> is less than two.</exception>
    /// <exception cref="InvalidDataException">A graph cannot be installed on a node: its shader compilation failed, or
    /// its plan is not one a node runs.</exception>
    public static bool TryCreate(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, string root, RenderGraphPackageRecorders packages, IGpuDeviceContext deviceContext, bool hostsOnDirectX, [NotNullWhen(returnValue: true)] out RenderGraphRuntime? runtime, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal, uint inFlightFrames = DefaultInFlightFrames) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: graphs);
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: packages);
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

        try {
            for (var index = 0; (index < graphs.Count); index++) {
                var instance = set.Instances[index];

                if (instance.Kind != RenderGraphInstanceKind.External) {
                    continue;
                }

                if (packages.ServesSource(package: instance.ExternalPackage!)) {
                    (sources[index], nodes[index]) = CreateSource(
                        deviceContext: deviceContext,
                        hostsOnDirectX: hostsOnDirectX,
                        inFlightFrames: inFlightFrames,
                        instance: instance,
                        packages: packages
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

                if (set.Instances[index].Kind != RenderGraphInstanceKind.Graph) {
                    continue;
                }

                nodes[index] = CreateNode(
                    deviceContext: deviceContext,
                    hostsOnDirectX: hostsOnDirectX,
                    inFlightFrames: inFlightFrames,
                    name: set.Instances[index].Name,
                    packages: packages
                );

                if (graphs[index] is { } graph) {
                    nodes[index]!.Swap(pipeline: graph.Pipeline);
                    installed[index] = graph;
                }
            }

            refusal = null;
            runtime = new RenderGraphRuntime(
                device: deviceContext,
                graphs: installed,
                hostsOnDirectX: hostsOnDirectX,
                inFlightFrames: inFlightFrames,
                inputs: inputs,
                nodes: nodes,
                packages: packages,
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
    // that is not an image, or names a package neither an external producer nor an upload serves.
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
                        : "names a package neither an external producer nor an upload serves")));

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
                hosted: (set.Instances[index].Kind == RenderGraphInstanceKind.External),
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

        refusal = null;

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
    private static ShaderPipelineRenderNode CreateNode(string name, RenderGraphPackageRecorders packages, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint inFlightFrames) => new(
        deviceContext: deviceContext,
        height: 1,
        hostsOnDirectX: hostsOnDirectX,
        inFlightFrames: inFlightFrames,
        name: name,
        outputLayout: GpuImageLayout.ShaderReadOnly,
        packages: packages,
        width: 1
    );
    // What an instance publishes to its consumers: its graph's default output as its node presents it, or its external
    // producer's images; nothing known for a graph instance whose graph is not installed yet.
    private static Published? PublishedBy(RenderGraphRuntimeGraph? graph, IRenderGraphExternalProducer? producer) {
        if (producer is not null) {
            return new Published(
                Format: PixelFormatOf(format: producer.Format),
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
    // Why a consumer's external version cannot bind what its producer publishes, or null when it can: an image of
    // another format, or a buffer larger than the producer's. A producer that publishes nothing known yet, a graph instance
    // whose graph is not installed, is checked when its graph installs.
    private static (string Declared, string Published)? Mismatch(ShaderPipelineResource declaration, Published? published) {
        if (published is not { } known) {
            return null;
        }
        if (declaration.Kind == ShaderPipelineResourceKind.Buffer) {
            var size = declaration.SizeBytes.GetValueOrDefault();

            return ((size > known.SizeBytes)
                ? ($"a {size}-byte buffer", $"a {known.SizeBytes}-byte buffer")
                : null);
        }

        var format = ShaderPipelineRenderNode.ParseFormat(format: declaration.Format);

        return ((format != known.Format)
            ? ($"{format}", $"{known.Format}")
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

        if (bindings.Length == 0) {
            return true;
        }

        var node = m_nodes[index]!;
        var consumer = m_set.Instances[index].Name;

        foreach (var binding in bindings) {
            if (binding.Kind != ShaderPipelineResourceKind.Buffer) {
                continue;
            }
            if (OutputAt(
                frame: FrameOf(
                    consumer: consumer,
                    producer: binding.ProducerName,
                    schedule: schedule
                ),
                producer: binding.Producer
            ).Buffer is not { } buffer) {
                return false;
            }

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
                if (producer.TryAcquireOutput(output: out var external)) {
                    node.BindImage(
                        image: new ShaderPipelineExternalImage(
                            Format: PixelFormatOf(format: external.Image.Format),
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
                producer: binding.Producer
            );

            if (output.Image.IsSameDeviceImage) {
                node.BindImage(
                    image: new ShaderPipelineExternalImage(
                        Format: PixelFormatOf(format: output.Image.Format),
                        Height: output.Image.Height,
                        ImageHandle: output.Image.ImageHandle,
                        ImageViewHandle: output.Image.ImageViewHandle,
                        Layout: output.Layout,
                        Width: output.Image.Width
                    ),
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
    // The newest completed output of a producer that is no newer than the frame asked for.
    private Output OutputAt(int producer, long frame) {
        var current = m_current[producer];

        if (
            (current.Frame >= 0) &&
            (current.Frame <= frame)
        ) {
            return current;
        }

        var previous = m_previous[producer];

        return (((previous.Frame >= 0) && (previous.Frame <= frame))
            ? previous
            : Output.None
        );
    }
    private void Release() {
        Array.Fill(
            array: m_current,
            value: Output.None
        );
        Array.Fill(
            array: m_previous,
            value: Output.None
        );
        Array.Clear(array: m_standInReads);
        m_history = RenderGraphHistory.Empty(set: m_set);
        m_latest = null;
        m_unproduced = 0;
    }
    // Why a capture of an instance would not be served by the frame the runtime produces now, or null when it would.
    private string? ReasonOf(int index) {
        var name = m_set.Instances[index].Name;

        if (m_producers[index] is { } producer) {
            return ((producer.NotReadyReason is { } reason)
                ? $"the instance '{name}' has produced no output: {reason}"
                : null);
        }
        if (m_sources[index]?.Fault is { } fault) {
            return $"the instance '{name}' has produced no output: {fault}";
        }
        if (
            !m_nodes[index]!.IsReady ||
            (m_current[index].Frame < 0)
        ) {
            return $"the instance '{name}' has produced no output";
        }

        return ((m_standInReads[index] is { } producerName)
            ? $"the instance '{name}' has rendered only over a stand-in for '{producerName}', which has produced no output"
            : null);
    }
    // Arms a capture of one instance on the runtime's one slot.
    private void Arm(int index, FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
        m_captureInstance = index;
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
        ReleaseStandIns(wait: true);
    }
    /// <summary>Releases every instance's device objects after the device was lost and recreated, so nothing of the old
    /// device survives: each node releases its graph and package recorders, the stand-ins are released, and every
    /// instance starts again from no completed output and no scheduling history, so the next frame renders every
    /// instance something shows and rebuilds it on the new device. A capture still armed on the runtime is refused
    /// (<see cref="CaptureRequestSlot.RefuseForDeviceLoss"/>), as the root's node refuses one forwarded to it.</summary>
    public void OnDeviceLost() {
        m_capture.RefuseForDeviceLoss();

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

        ReleaseStandIns(wait: false);
        Release();
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
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        var schedule = m_schedules[m_turn];
        var prior = m_history;
        var scheduled = WithSourceStates(frame: in frame);

        RenderGraphScheduler.Schedule(
            frame: scheduled,
            history: prior,
            schedule: schedule,
            set: m_set
        );
        m_turn ^= 1;
        m_history = schedule.Next;
        m_latest = schedule;
        m_unproduced = 0;

        var renders = schedule.Renders;

        for (var position = 0; (position < renders.Count); position++) {
            var index = renders[position];
            var row = schedule.Instances[index];

            // An external producer submits through its own ring, at the scheduled extent, before its consumers render. A
            // capture of it moves to it first, and it serves the capture from the next frame it produces. A render it
            // could not produce is withdrawn from the history, so its cadence counts from its last completed frame and a
            // source that renders once is asked again on the next frame.
            if (m_producers[index] is { } producer) {
                if (index == m_captureInstance) {
                    m_capture.Forward(target: producer);
                }
                if (
                    (row.Width <= 0) ||
                    (row.Height <= 0) ||
                    !producer.Produce(
                        context: in context,
                        height: ((uint)row.Height),
                        width: ((uint)row.Width)
                    )
                ) {
                    m_unproduced++;
                    schedule.Next.Withdraw(
                        index: index,
                        previous: prior
                    );
                }

                continue;
            }

            var node = m_nodes[index]!;
            var source = m_sources[index];

            // An uploaded source writes its image for the frame's tick into its region; one with no image is withdrawn,
            // as an external producer's render is, so its cadence counts from its last converted frame.
            if (
                (source is not null) &&
                !source.TryWrite(
                    device: m_device,
                    inFlightFrames: m_inFlightFrames,
                    node: node,
                    tick: frame.Tick
                )
            ) {
                m_unproduced++;
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

                continue;
            }
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
            if (
                (index == m_captureInstance) &&
                node.IsReady &&
                (m_standInReads[index] is null)
            ) {
                m_capture.Forward(target: node);
            }

            var submitted = node.FrameCounter;
            var surface = node.ProduceFrame(context: in context);

            if (node.FrameCounter == submitted) {
                m_unproduced++;

                // A source whose conversion has not built yet is asked again, since its cadence may never ask twice.
                if (source is not null) {
                    schedule.Next.Withdraw(
                        index: index,
                        previous: prior
                    );
                }

                continue;
            }

            m_previous[index] = m_current[index];
            m_current[index] = new Output(
                Buffer: node.LatestOutputBuffer(),
                Frame: frame.Index,
                Image: surface,
                Layout: node.PublishedLayout
            );
        }

        ConvertForCapture(
            context: in context,
            frame: frame.Index,
            schedule: schedule,
            tick: frame.Tick
        );

        return RootImage();
    }

    // The root's latest completed image: a graph root's output, or an external root's latest output, whose acquisition is
    // released at once, since the surface is valid only until the next frame, the first time the producer can replace it.
    private Surface RootImage() {
        if (m_producers[m_root] is not { } producer) {
            return m_current[m_root].Image;
        }
        if (!producer.TryAcquireOutput(output: out var output)) {
            return default;
        }

        output.Lease.Retire();

        return output.Image;
    }

    /// <inheritdoc/>
    /// <remarks>The capture reads the root instance, as one armed through its <see cref="CaptureTarget"/> does.</remarks>
    public void RequestCapture(FrameCaptureRequest request) => Arm(
        index: m_root,
        request: request
    );
    /// <summary>Returns the capture target of one instance. A capture armed on it is served by the first frame after it
    /// is armed on which a graph instance renders an installed graph with every image input bound to a completed output,
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
    /// as the refusal of a capture that waited on it reads: the instance has no completed output, or its latest render
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
    // One input resolved at install: the version it binds and the producer whose output it reads.
    private readonly record struct Binding(string Version, int Producer, string ProducerName, ShaderPipelineResourceKind Kind, GpuPixelFormat Format);
    // One completed output of an instance: the frame it belongs to, its published image and the layout it is in, and its
    // buffer when it is one.
    private readonly record struct Output(long Frame, Surface Image, GpuImageLayout Layout, IGpuBuffer? Buffer) {
        public static Output None => new(
            Buffer: null,
            Frame: -1,
            Image: default,
            Layout: GpuImageLayout.Undefined
        );
    }
}
