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
    /// <summary>The root names no instance, or an instance whose output is not an image.</summary>
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
/// Each instance counts its own work (<see cref="Work"/>). The root instance is what the display shows: the runtime's
/// output is its latest completed image, and a capture armed on the runtime is served from it by the root's node on a
/// frame the root renders. A steady frame, one whose schedule and extents repeat an earlier one, allocates nothing.
/// </para>
/// </summary>
public sealed partial class RenderGraphRuntime : ICaptureRequestTarget, IDisposable {
    private readonly CaptureRequestSlot m_capture = new();
    private readonly Output[] m_current;
    private readonly IGpuDeviceContext m_device;
    private readonly Binding[][] m_inputs;
    private readonly ShaderPipelineRenderNode[] m_nodes;
    private readonly Output[] m_previous;
    private readonly int m_root;
    private readonly RenderGraphSchedule[] m_schedules;
    private readonly RenderGraphInstanceSet m_set;

    private bool m_disposed;
    private RenderGraphHistory m_history;
    private RenderGraphSchedule? m_latest;
    private int m_turn;
    private int m_unproduced;

    private RenderGraphRuntime(RenderGraphInstanceSet set, ShaderPipelineRenderNode[] nodes, Binding[][] inputs, int root, IGpuDeviceContext device) {
        m_current = new Output[nodes.Length];
        m_device = device;
        m_history = RenderGraphHistory.Empty(set: set);
        m_inputs = inputs;
        m_nodes = nodes;
        m_previous = new Output[nodes.Length];
        m_root = root;
        m_schedules = [
            new RenderGraphSchedule(set: set),
            new RenderGraphSchedule(set: set),
        ];
        m_set = set;

        Array.Fill(
            array: m_current,
            value: Output.None
        );
        Array.Fill(
            array: m_previous,
            value: Output.None
        );
    }

    /// <summary>Gets whether every instance the latest frame scheduled produced its output: false before the first
    /// frame, and while any scheduled instance's graph is still building.</summary>
    public bool IsSettled => ((m_latest is not null) && (m_unproduced == 0));
    /// <summary>Gets the instance set the runtime schedules.</summary>
    public RenderGraphInstanceSet Instances => m_set;
    /// <summary>Gets the latest frame's schedule, or <see langword="null"/> before the first frame.</summary>
    public RenderGraphSchedule? Latest => m_latest;
    /// <inheritdoc/>
    public string? PendingCapturePath => (m_capture.PendingPath ?? m_nodes[m_root].PendingCapturePath);
    /// <summary>Gets the root instance's name: the instance the display shows and captures read.</summary>
    public string Root => m_set.Instances[m_root].Name;
    /// <summary>Gets why a capture armed on the runtime would not be served by the frame it produces now, phrased as the
    /// refusal of a capture that waited on it reads, or <see langword="null"/> once the root instance has a completed
    /// output.</summary>
    public string? UnservedCaptureReason => ((m_nodes[m_root].IsReady && (m_current[m_root].Frame >= 0))
        ? null
        : $"the root instance '{Root}' has produced no output"
    );

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
    private static bool TryResolve(RenderGraphInstanceSet set, int index, RenderGraphRuntimeGraph graph, RenderGraphPackageRecorders packages, [NotNullWhen(returnValue: true)] out Binding[]? bindings, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
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
                    package: unserved.Declaration.Source,
                    pass: unserved.Name
                ),
                instance.Name,
                unserved.Name,
                unserved.Declaration.Source
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

        if (external.Keys.FirstOrDefault(predicate: name => !bound.Contains(item: name)) is { } unbound) {
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

    /// <summary>Installs a set's graphs: one node per instance, each holding its graph as a candidate that builds when
    /// the instance first renders.</summary>
    /// <param name="set">The instances.</param>
    /// <param name="graphs">Each instance's graph, parallel to <see cref="RenderGraphInstanceSet.Instances"/>.</param>
    /// <param name="root">The name of the instance the display shows and captures read.</param>
    /// <param name="packages">The recorders the graphs' package passes run through.</param>
    /// <param name="deviceContext">The device every instance records on.</param>
    /// <param name="hostsOnDirectX">Whether the device is Direct3D 12.</param>
    /// <param name="runtime">The runtime, when this returns <see langword="true"/>. The caller owns it.</param>
    /// <param name="refusal">Why the graphs were refused, when this returns <see langword="false"/>; nothing was
    /// created then.</param>
    /// <param name="inFlightFrames">Each instance's frames in flight, at least two, since an instance's history and its
    /// previous-frame reads live in its previous frame slot.</param>
    /// <returns><see langword="true"/> when the graphs installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="graphs"/>, one of its entries,
    /// <paramref name="root"/>, <paramref name="packages"/> or <paramref name="deviceContext"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inFlightFrames"/> is less than two.</exception>
    /// <exception cref="InvalidDataException">A graph cannot be installed on a node: its shader compilation failed, or
    /// its plan is not one a node runs.</exception>
    public static bool TryCreate(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph> graphs, string root, RenderGraphPackageRecorders packages, IGpuDeviceContext deviceContext, bool hostsOnDirectX, [NotNullWhen(returnValue: true)] out RenderGraphRuntime? runtime, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal, uint inFlightFrames = 3) {
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

        var inputs = new Binding[graphs.Count][];

        for (var index = 0; (index < graphs.Count); index++) {
            var graph = graphs[index];

            ArgumentNullException.ThrowIfNull(
                argument: graph,
                paramName: nameof(graphs)
            );

            if (!TryResolve(
                bindings: out var bindings,
                graph: graph,
                index: index,
                packages: packages,
                refusal: out refusal,
                set: set
            )) {
                return false;
            }

            inputs[index] = bindings;
        }

        var nodes = new ShaderPipelineRenderNode[graphs.Count];

        try {
            for (var index = 0; (index < graphs.Count); index++) {
                // The extent is a placeholder: the instance's first render requests its scheduled extent before the node
                // builds anything.
                nodes[index] = new ShaderPipelineRenderNode(
                    deviceContext: deviceContext,
                    height: 1,
                    hostsOnDirectX: hostsOnDirectX,
                    inFlightFrames: inFlightFrames,
                    name: set.Instances[index].Name,
                    outputLayout: GpuImageLayout.ShaderReadOnly,
                    packages: packages,
                    width: 1
                );
                nodes[index].Swap(pipeline: graphs[index].Pipeline);
            }
        } catch {
            foreach (var node in nodes) {
                node?.Dispose();
            }

            throw;
        }

        refusal = null;
        runtime = new RenderGraphRuntime(
            device: deviceContext,
            inputs: inputs,
            nodes: nodes,
            root: rootIndex,
            set: set
        );

        return true;
    }

    // Binds each of an instance's inputs to the frame of its producer's output the schedule names, or an image input to a
    // stand-in when the producer has no completed output of that frame or earlier. A buffer has no stand-in: an
    // instance whose buffer producer has no completed output does not render, and false says so.
    private bool Bind(int index, RenderGraphSchedule schedule) {
        var bindings = m_inputs[index];
        var complete = true;

        if (bindings.Length == 0) {
            return complete;
        }

        var node = m_nodes[index];
        var consumer = m_set.Instances[index].Name;
        var reads = schedule.Reads;

        foreach (var binding in bindings) {
            var frame = -1L;

            for (var read = 0; (read < reads.Count); read++) {
                var row = reads[read];

                if (
                    ReferenceEquals(
                        objA: row.Consumer,
                        objB: consumer
                    ) &&
                    ReferenceEquals(
                        objA: row.Producer,
                        objB: binding.ProducerName
                    )
                ) {
                    frame = row.Frame;

                    break;
                }
            }

            var output = OutputAt(
                frame: frame,
                producer: binding.Producer
            );

            if (binding.Kind == ShaderPipelineResourceKind.Buffer) {
                if (output.Buffer is { } buffer) {
                    node.BindBuffer(
                        buffer: buffer,
                        name: binding.Version
                    );
                } else {
                    complete = false;
                }

                continue;
            }

            node.BindImage(
                image: (output.Image.IsSameDeviceImage
                    ? new ShaderPipelineExternalImage(
                        Format: PixelFormatOf(format: output.Image.Format),
                        Height: output.Image.Height,
                        ImageHandle: output.Image.ImageHandle,
                        ImageViewHandle: output.Image.ImageViewHandle,
                        Layout: GpuImageLayout.ShaderReadOnly,
                        Width: output.Image.Width
                    )
                    : StandInFor(format: binding.Format)),
                name: binding.Version
            );
        }

        return complete;
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
        m_history = RenderGraphHistory.Empty(set: m_set);
        m_latest = null;
        m_unproduced = 0;
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

        foreach (var node in m_nodes) {
            node.Dispose();
        }

        ReleaseStandIns(wait: true);
    }
    /// <summary>Releases every instance's device objects after the device was lost and recreated, so nothing of the old
    /// device survives: each node releases its graph and package recorders, the stand-ins are released, and every
    /// instance starts again from no completed output and no scheduling history, so the next frame renders every
    /// instance something shows and rebuilds it on the new device. A capture still armed on the runtime is refused
    /// (<see cref="CaptureRequestSlot.RefuseForDeviceLoss"/>), as the root's node refuses one forwarded to it.</summary>
    public void OnDeviceLost() {
        m_capture.RefuseForDeviceLoss();

        foreach (var node in m_nodes) {
            node.OnDeviceLost();
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

        RenderGraphScheduler.Schedule(
            frame: frame,
            history: m_history,
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
            var node = m_nodes[index];
            var row = schedule.Instances[index];

            if (!Bind(
                index: index,
                schedule: schedule
            )) {
                m_unproduced++;

                continue;
            }
            if (
                (row.Width > 0) &&
                (row.Height > 0)
            ) {
                node.Resize(
                    height: ((uint)row.Height),
                    width: ((uint)row.Width)
                );
            }
            // A capture moves to the root's node only once that node renders a graph, so the frame it produces is the
            // one the capture reads; until then it stays armed here, where UnservedCaptureReason explains it.
            if (
                (index == m_root) &&
                node.IsReady
            ) {
                m_capture.Forward(target: node);
            }

            var submitted = node.FrameCounter;
            var surface = node.ProduceFrame(context: in context);

            if (node.FrameCounter == submitted) {
                m_unproduced++;

                continue;
            }

            m_previous[index] = m_current[index];
            m_current[index] = new Output(
                Buffer: node.LatestOutputBuffer(),
                Frame: frame.Index,
                Image: surface
            );
        }

        return m_current[m_root].Image;
    }
    /// <inheritdoc/>
    /// <remarks>The capture is served from the root instance's output by the first frame after this call on which the
    /// root renders an installed graph. A requester that stops waiting withdraws it with
    /// <see cref="FrameCaptureRequest.TryFail"/>, and the runtime then drops it.</remarks>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
    }
    /// <summary>Returns the node an instance renders through, for inspection.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The node.</returns>
    public ShaderPipelineRenderNode Node(int instance) => m_nodes[instance];
    /// <summary>Returns an instance's counted GPU work: its own submissions, per pass of its graph, shader and package
    /// passes alike.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The instance's work source.</returns>
    public IGpuWorkSource Work(int instance) => m_nodes[instance];

    // One input resolved at install: the version it binds and the producer whose output it reads.
    private readonly record struct Binding(string Version, int Producer, string ProducerName, ShaderPipelineResourceKind Kind, GpuPixelFormat Format);
    // One completed output of an instance: the frame it belongs to, its published image, and its buffer when it is one.
    private readonly record struct Output(long Frame, Surface Image, IGpuBuffer? Buffer) {
        public static Output None => new(
            Buffer: null,
            Frame: -1,
            Image: default
        );
    }
}
