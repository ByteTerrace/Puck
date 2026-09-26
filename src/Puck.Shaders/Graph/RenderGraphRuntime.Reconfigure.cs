using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    // Retired graph instances a kept consumer still holds (Retire).
    private readonly List<RetiredProducer> m_retiredProducers = [];

    /// <summary>Replaces the instance set the runtime runs. An instance of the new set that has the same name and kind as
    /// one of the old set (and, for an external instance, the same package and settings) keeps its node or producer, and
    /// with it its installed graph, history, captures and latest output; every other instance of the new set starts as
    /// <see cref="TryCreate"/> starts one, and every old instance absent from it is disposed once the device has finished
    /// every submission that may sample its output. An instance a kept consumer's installed graph still reads, which it
    /// keeps presenting while its replacement builds, is disposed only once that consumer can no longer sample it
    /// (<see cref="ShaderPipelineRenderNode.HoldBinding(string, GpuImageLease)"/>, <see cref="RetiredProducers"/>): a
    /// graph instance's output as the consumer bound it, and an external producer's latest output, acquired once more and
    /// bound for every frame until then. Scheduling history restarts, so the next frame renders everything it
    /// shows.</summary>
    /// <param name="set">The new instances.</param>
    /// <param name="graphs">Each new instance's graph, parallel to <see cref="RenderGraphInstanceSet.Instances"/>:
    /// <see langword="null"/> for an external instance, for a new graph instance whose graph is not compiled yet, and for
    /// a kept graph instance that keeps the graph it has; a graph given for a kept instance replaces its own, and one
    /// that keeps its pipeline and moves only its inputs rebinds them and builds nothing.</param>
    /// <param name="root">The name of the instance the display shows and captures read.</param>
    /// <param name="refusal">Why the set was refused, when this returns <see langword="false"/>; the runtime is
    /// unchanged then, and every producer created to learn its format was disposed.</param>
    /// <returns><see langword="true"/> when the new set runs from the next frame.</returns>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="set"/>, <paramref name="graphs"/> or
    /// <paramref name="root"/> is <see langword="null"/>.</exception>
    public bool TryReconfigure(RenderGraphInstanceSet set, IReadOnlyList<RenderGraphRuntimeGraph?> graphs, string root, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: graphs);
        ArgumentNullException.ThrowIfNull(argument: root);

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
            packages: m_packages,
            set: set
        ) is { } external) {
            refusal = external;

            return false;
        }

        var count = set.Instances.Count;
        var kept = new int[count];
        var effective = new RenderGraphRuntimeGraph?[count];
        var producers = new IRenderGraphExternalProducer?[count];
        var created = new IRenderGraphExternalProducer?[count];

        for (var index = 0; (index < count); index++) {
            var instance = set.Instances[index];
            var old = m_set.IndexOf(name: instance.Name);

            kept[index] = (((old >= 0) && Keeps(
                instance: instance,
                old: m_set.Instances[old]
            ))
                ? old
                : -1);
            effective[index] = (graphs[index] ?? ((kept[index] >= 0)
                ? m_graphs[kept[index]]
                : null));
        }

        try {
            for (var index = 0; (index < count); index++) {
                if (set.Instances[index].Kind != RenderGraphInstanceKind.External) {
                    continue;
                }

                producers[index] = ((kept[index] >= 0)
                    ? m_producers[kept[index]]
                    : (created[index] = CreateProducer(
                        deviceContext: m_device,
                        hostsOnDirectX: m_hostsOnDirectX,
                        instance: set.Instances[index],
                        packages: m_packages
                    )));
            }
        } catch {
            DisposeAll(
                nodes: [],
                producers: created
            );

            throw;
        }

        if (!TryBindAll(
            graphs: effective,
            inputs: out var inputs,
            packages: m_packages,
            producers: producers,
            refusal: out refusal,
            set: set
        )) {
            DisposeAll(
                nodes: [],
                producers: created
            );

            return false;
        }

        // Everything that can fail is prepared before anything of the running set changes: the new graph instances'
        // nodes, a check of every graph a node will be handed, and the holds the retired instances need. A failure
        // disposes what was prepared, retires what it acquired, and leaves the running set as it was.
        var nodes = new ShaderPipelineRenderNode?[count];
        var retired = RetiredBy(kept: kept);
        var holds = new List<PlannedHold>();
        var retiring = new RetiredProducer?[retired.Length];

        try {
            for (var index = 0; (index < count); index++) {
                if (set.Instances[index].Kind != RenderGraphInstanceKind.Graph) {
                    continue;
                }

                nodes[index] = ((kept[index] >= 0)
                    ? m_nodes[kept[index]]
                    : CreateNode(
                        deviceContext: m_device,
                        hostsOnDirectX: m_hostsOnDirectX,
                        inFlightFrames: m_inFlightFrames,
                        name: set.Instances[index].Name,
                        packages: m_packages
                    ));

                if (SwappedIn(
                    graph: effective[index],
                    old: kept[index]
                ) is { } pipeline) {
                    nodes[index]!.RequireSwappable(pipeline: pipeline);
                }
            }

            PlanHolds(
                holds: holds,
                kept: kept,
                retired: retired,
                retiring: retiring
            );
        } catch {
            foreach (var hold in holds) {
                hold.Abandon();
            }
            for (var index = 0; (index < count); index++) {
                if (kept[index] < 0) {
                    nodes[index]?.Dispose();
                }
            }

            DisposeAll(
                nodes: [],
                producers: created
            );

            throw;
        }

        // Nothing below fails before the new set runs.
        var current = new Output[count];
        var previous = new Output[count];

        for (var index = 0; (index < count); index++) {
            var old = kept[index];

            current[index] = ((old >= 0)
                ? m_current[old]
                : Output.None);
            previous[index] = ((old >= 0)
                ? m_previous[old]
                : Output.None);

            // A graph that keeps the instance's pipeline and moves only its inputs rebinds them and builds nothing.
            if (SwappedIn(
                graph: effective[index],
                old: old
            ) is { } pipeline) {
                nodes[index]!.Swap(pipeline: pipeline);
            }
        }

        foreach (var hold in holds) {
            hold.Apply();
        }

        var oldNodes = m_nodes;
        var oldProducers = m_producers;
        var captured = m_set.Instances[m_captureInstance].Name;

        m_captureInstance = set.IndexOf(name: captured);

        if (m_captureInstance < 0) {
            m_capture.Refuse(error: new InvalidOperationException(message: $"The render graph no longer has an instance '{captured}'."));
            m_captureInstance = rootIndex;
        }

        m_current = current;
        m_graphs = effective;
        m_history = RenderGraphHistory.Empty(set: set);
        m_inputs = inputs;
        m_latest = null;
        m_nodes = nodes;
        m_previous = previous;
        m_producers = producers;
        m_root = rootIndex;
        m_schedules = [
            new RenderGraphSchedule(set: set),
            new RenderGraphSchedule(set: set),
        ];
        m_set = set;
        m_standInReads = new string?[count];
        m_unproduced = 0;
        refusal = null;

        Retire(
            nodes: oldNodes,
            producers: oldProducers,
            retired: retired,
            retiring: retiring
        );

        return true;
    }
    /// <summary>Installs a graph on one graph instance, replacing the one it has: its inputs are resolved against what
    /// every producer publishes, and its consumers' inputs against what the new graph publishes, and the instance's node
    /// builds it off the frame thread and keeps presenting the graph it has until the new one installs. A graph that
    /// keeps the instance's pipeline and moves only its inputs rebinds them from the next frame and builds
    /// nothing.</summary>
    /// <param name="instance">The graph instance's name.</param>
    /// <param name="graph">The graph.</param>
    /// <param name="refusal">Why the graph was refused, when this returns <see langword="false"/>; the instance keeps
    /// the graph it has.</param>
    /// <returns><see langword="true"/> when the node builds the graph.</returns>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> or <paramref name="graph"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The set has no instance of that name, or it is an external
    /// instance.</exception>
    /// <exception cref="InvalidDataException">The graph cannot be installed on a node: its shader compilation failed, or
    /// its plan is not one a node runs.</exception>
    public bool TryInstall(string instance, RenderGraphRuntimeGraph graph, [NotNullWhen(returnValue: false)] out RenderGraphRuntimeRefusal? refusal) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: graph);

        var index = IndexOf(instance: instance);
        var node = Node(instance: index);
        var graphs = ((RenderGraphRuntimeGraph?[])m_graphs.Clone());

        graphs[index] = graph;

        if (!TryBindAll(
            graphs: graphs,
            inputs: out var inputs,
            packages: m_packages,
            producers: m_producers,
            refusal: out refusal,
            set: m_set
        )) {
            return false;
        }

        if (!ReferenceEquals(
            objA: graph.Pipeline,
            objB: m_graphs[index]?.Pipeline
        )) {
            node.Swap(pipeline: graph.Pipeline);
        }

        m_graphs = graphs;
        m_inputs = inputs;

        return true;
    }
    /// <summary>Returns the node a graph instance renders through, or <see langword="null"/> when the set has no graph
    /// instance of that name.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public ShaderPipelineRenderNode? NodeOf(string instance) {
        ArgumentNullException.ThrowIfNull(argument: instance);

        var index = m_set.IndexOf(name: instance);

        return ((index >= 0)
            ? m_nodes[index]
            : null);
    }

    // Whether an instance of a new set continues an instance of the old one of its name.
    // A source opened with other settings is another image, so it is a new producer under the same name.
    private static bool Keeps(RenderGraphInstance instance, RenderGraphInstance old) => (
        (instance.Kind == old.Kind) &&
        string.Equals(
            a: instance.ExternalPackage,
            b: old.ExternalPackage,
            comparisonType: StringComparison.Ordinal
        ) &&
        ImageSourceSettings.Equal(
            left: instance.Settings,
            right: old.Settings
        )
    );
    // Which old instances the new set does not keep.
    private bool[] RetiredBy(int[] kept) {
        var retired = new bool[m_set.Instances.Count];

        Array.Fill(
            array: retired,
            value: true
        );

        foreach (var old in kept) {
            if (old >= 0) {
                retired[old] = false;
            }
        }

        return retired;
    }
    // The pipeline a new instance's node is handed: its graph's, unless the node it keeps already has that pipeline.
    private CompiledShaderPipeline? SwappedIn(RenderGraphRuntimeGraph? graph, int old) => (((graph is { } next) && !ReferenceEquals(
        objA: next.Pipeline,
        objB: ((old >= 0)
            ? m_graphs[old]?.Pipeline
            : null)
    ))
        ? next.Pipeline
        : null);
    // Plans the holds a reconfiguration's retired instances need, without changing anything of the running set. A kept
    // consumer keeps presenting its installed graph while a replacement builds, and that graph samples what the consumer
    // last bound, so each retired instance a kept consumer has bound is held (HoldBinding) until every consumer holding
    // it releases it: on installing a graph that no longer reads it, on a newer binding, or at the consumer's release. A
    // consumer that never bound the name (one that has not rendered) samples nothing of it and holds nothing. A retired
    // external producer's binding was leased for one frame, so the consumer is handed its latest output once more, bound
    // for every frame until the hold releases, and the hold retires that acquisition first; a producer with no output
    // leaves the consumer on a stand-in, as a frame binding it would, and holds nothing.
    private void PlanHolds(List<PlannedHold> holds, int[] kept, bool[] retired, RetiredProducer?[] retiring) {
        foreach (var consumer in kept) {
            if (
                (consumer < 0) ||
                (m_nodes[consumer] is not { } node)
            ) {
                continue;
            }

            foreach (var binding in m_inputs[consumer]) {
                if (
                    !retired[binding.Producer] ||
                    !node.IsBound(name: binding.Version)
                ) {
                    continue;
                }

                if (m_producers[binding.Producer] is { } external) {
                    if (binding.Kind == ShaderPipelineResourceKind.Buffer) {
                        continue;
                    }
                    if (
                        !external.TryAcquireOutput(output: out var output) ||
                        (output.Image.ImageHandle == 0) ||
                        (output.Image.ImageViewHandle == 0)
                    ) {
                        output.Lease.Retire();
                        holds.Add(item: new PlannedHold(
                            acquired: default,
                            consumer: node,
                            image: StandInFor(format: binding.Format),
                            name: binding.Version,
                            producer: null
                        ));

                        continue;
                    }

                    var hold = (retiring[binding.Producer] ??= new RetiredProducer(
                        dispose: external.Dispose,
                        lost: external.OnDeviceLost,
                        release: m_retiredProducers
                    ));

                    hold.Holds++;
                    holds.Add(item: new PlannedHold(
                        acquired: output.Lease,
                        consumer: node,
                        image: new ShaderPipelineExternalImage(
                            Format: PixelFormatOf(format: output.Image.Format),
                            Height: output.Image.Height,
                            ImageHandle: output.Image.ImageHandle,
                            ImageViewHandle: output.Image.ImageViewHandle,
                            Layout: output.Layout,
                            Width: output.Image.Width
                        ),
                        name: binding.Version,
                        producer: hold
                    ));
                } else if (m_nodes[binding.Producer] is { } producer) {
                    var hold = (retiring[binding.Producer] ??= new RetiredProducer(
                        dispose: producer.DisposeRetired,
                        lost: null,
                        release: m_retiredProducers
                    ));

                    hold.Holds++;
                    holds.Add(item: new PlannedHold(
                        acquired: default,
                        consumer: node,
                        image: null,
                        name: binding.Version,
                        producer: hold
                    ));
                }
            }
        }
    }
    // Retires the old set's instances the new set does not keep, once the new set runs: a held one waits for its last
    // hold, and the rest are disposed after the device has finished every submission that may sample their outputs,
    // since a kept consumer's frame in flight may still read them. Every disposal is attempted, and the failures are
    // thrown together after the last.
    private void Retire(ShaderPipelineRenderNode?[] nodes, IRenderGraphExternalProducer?[] producers, bool[] retired, RetiredProducer?[] retiring) {
        if (!retired.Contains(value: true)) {
            return;
        }

        m_device.TryWaitIdle();

        List<Exception>? failures = null;

        for (var old = 0; (old < retired.Length); old++) {
            if (!retired[old]) {
                continue;
            }
            if (retiring[old] is { Holds: > 0 } hold) {
                m_retiredProducers.Add(item: hold);

                continue;
            }

            try {
                nodes[old]?.Dispose();
                producers[old]?.Dispose();
            } catch (Exception error) {
                (failures ??= []).Add(item: error);
            }
        }

        if (failures is not null) {
            throw new AggregateException(
                innerExceptions: failures,
                message: "The render graph's new instance set runs, but retiring an old instance failed."
            );
        }
    }

    // A hold planned for a kept consumer, applied once the new set runs or abandoned with it.
    private sealed class PlannedHold(ShaderPipelineRenderNode consumer, string name, RetiredProducer? producer, ShaderPipelineExternalImage? image, GpuImageLease acquired) {
        // Hands the consumer the hold: its binding held as it is, or rebound to the image acquired for it.
        public void Apply() {
            if (image is not { } bound) {
                consumer.HoldBinding(
                    lease: new GpuImageLease(
                        ImageViewHandle: 0,
                        Release: producer!.Release
                    ),
                    name: name
                );

                return;
            }

            consumer.HoldBinding(
                image: bound,
                lease: ((producer is { } held)
                    ? new GpuImageLease(
                        ImageViewHandle: bound.ImageViewHandle,
                        Release: token => {
                            acquired.Retire();
                            held.Release(token: token);
                        }
                    )
                    : default),
                name: name
            );
        }
        // Retires the acquisition made for a hold that is never applied.
        public void Abandon() => acquired.Retire();
    }

    /// <summary>Gets the instances a reconfiguration retired that are not yet disposed, because a kept consumer's
    /// installed graph still reads what it last bound of them.</summary>
    public int RetiredProducers => m_retiredProducers.Count;

    // A retired instance, a graph instance's node or an external producer, that kept consumers still hold, disposed once
    // the last of them releases it: each consumer releases its hold only after its own last submission sampling it has
    // completed, and the retired instance has made no submission since the retiring drain, so a node is disposed
    // without draining.
    private sealed class RetiredProducer(Action dispose, Action? lost, List<RetiredProducer> release) {
        private bool m_lost;

        public int Holds { get; set; }

        // A device loss reaches a held external producer when its consumers' loss releases it, before it is disposed.
        public void OnDeviceLost() => m_lost = true;
        public void Release(int token) {
            if (--Holds != 0) {
                return;
            }

            _ = release.Remove(item: this);

            if (m_lost) {
                lost?.Invoke();
            }

            dispose();
        }
    }
}
