using Puck.Hosting;

namespace Puck.Shaders;

// The images an external producer reads (the set refuses its buffer reads): before it produces, the runtime binds each
// read to the latest completed output of the instance read, an external producer's under the lease its acquisition
// returns and a graph instance's unleased, and hands the list to Produce. A read of the producer's own output therefore
// binds the output it completed before this frame. On a capture frame a previous-frame read of a tainted output binds
// nothing (Withholds). The producer takes the leases its submission samples; the rest are retired once Produce returns.
// A graph instance's reads that its graph binds to no version (an SDF view's screens) are bound the same way, after its
// graph's inputs, and handed to its package passes' recordings, whose recorders take what they sample.
public sealed partial class RenderGraphRuntime {
    // Each instance's external reads, or null for one that has none, for the set they were made for: an external
    // instance's every read, and a graph instance's reads of the producers its graph binds to no version.
    private RenderGraphExternalReads?[] m_externalReads = [];
    private RenderGraphInstanceSet? m_externalReadsSet;
    private Binding[][]? m_externalReadsInputs;

    // Binds an external instance's reads for the frame, or returns null when it reads nothing.
    private RenderGraphExternalReads? BindExternalReads(int index, RenderGraphSchedule schedule) {
        m_taintedReads[index] = null;

        return BindReads(
            index: index,
            schedule: schedule,
            unbound: false
        );
    }
    // Binds a graph instance's reads of the producers its graph binds to no version, which Bind has not, or returns null
    // when it has none or its graph runs no package that samples them. The taint they carry joins the taint Bind noted.
    private RenderGraphExternalReads? BindUnboundReads(int index, RenderGraphSchedule schedule) => (SamplesReads(graph: m_graphs[index])
        ? BindReads(
            index: index,
            schedule: schedule,
            unbound: true
        )
        : null);
    // Whether a graph runs a package pass whose package samples its instance's unbound reads.
    private bool SamplesReads(RenderGraphRuntimeGraph? graph) {
        if (graph is null) {
            return false;
        }

        var passes = graph.Pipeline.Plan.Passes;

        for (var index = 0; (index < passes.Count); index++) {
            if (
                (passes[index].Package is { } step) &&
                m_packages.TryGetFactory(
                    factory: out var factory,
                    package: step.Package
                ) &&
                factory.SamplesReads
            ) {
                return true;
            }
        }

        return false;
    }
    // Whether a graph instance's graph binds a version to a producer.
    private bool BindsProducer(int index, int producer) {
        foreach (var binding in m_inputs[index]) {
            if (binding.Producer == producer) {
                return true;
            }
        }

        return false;
    }
    private RenderGraphExternalReads? BindReads(int index, RenderGraphSchedule schedule, bool unbound) {
        // An install replaces the graphs' bindings, and with them which reads a graph leaves unbound.
        if (
            !ReferenceEquals(
                objA: m_externalReadsSet,
                objB: m_set
            ) ||
            !ReferenceEquals(
                objA: m_externalReadsInputs,
                objB: m_inputs
            )
        ) {
            m_externalReads = new RenderGraphExternalReads?[m_set.Instances.Count];
            m_externalReadsSet = m_set;
            m_externalReadsInputs = m_inputs;
        }

        var all = m_set.Reads[index];

        if (all.Count == 0) {
            return null;
        }

        // The edges bound here, in the set's read order: every edge, or a graph instance's unbound ones, which the list
        // made for the set fixes once.
        var reads = m_externalReads[index];

        if (reads is null) {
            var producers = new List<string>(capacity: all.Count);

            foreach (var edge in all) {
                if (
                    !unbound ||
                    !BindsProducer(
                        index: index,
                        producer: edge.Producer
                    )
                ) {
                    producers.Add(item: m_set.Instances[edge.Producer].Name);
                }
            }

            reads = new RenderGraphExternalReads(producers: producers);
            m_externalReads[index] = reads;
        }
        if (reads.Count == 0) {
            return null;
        }

        var consumer = m_set.Instances[index].Name;

        for (var edgeIndex = 0; (edgeIndex < all.Count); edgeIndex++) {
            var producer = all[edgeIndex].Producer;
            var previousFrame = all[edgeIndex].PreviousFrame;
            var position = reads.IndexOf(producer: m_set.Instances[producer].Name);

            if (position < 0) {
                continue;
            }

            var frame = FrameOf(
                consumer: consumer,
                producer: m_set.Instances[producer].Name,
                schedule: schedule
            );

            if (m_producers[producer] is { } external) {
                if (external.TryAcquireOutput(output: out var output)) {
                    m_producerTainted[producer] = output.Tainted;

                    if (Withholds(
                        previousFrame: previousFrame,
                        tainted: output.Tainted
                    )) {
                        output.Lease.Retire();

                        continue;
                    }

                    NoteTaint(
                        index: index,
                        producer: m_set.Instances[producer].Name,
                        tainted: output.Tainted
                    );
                    reads.Bind(
                        image: output.Image,
                        index: position,
                        layout: output.Layout,
                        lease: output.Lease,
                        tainted: output.Tainted
                    );
                } else if (unbound) {
                    NoteStandIn(frame: frame, index: index, producer: m_set.Instances[producer].Name);
                }

                continue;
            }

            // A read the frame does not show still binds the producer's latest output, so an image the display will
            // show again is never replaced by nothing.
            var completed = OutputAt(
                frame: ((frame < 0)
                    ? long.MaxValue
                    : frame),
                producer: producer
            );

            if (
                completed.Image.IsSameDeviceImage &&
                !Withholds(
                    previousFrame: previousFrame,
                    tainted: completed.Tainted
                )
            ) {
                NoteTaint(
                    index: index,
                    producer: m_set.Instances[producer].Name,
                    tainted: completed.Tainted
                );
                reads.Bind(
                    image: completed.Image,
                    index: position,
                    layout: completed.Layout,
                    lease: completed.Image.ImageViewHandle,
                    tainted: completed.Tainted
                );
            } else if (unbound && !completed.Image.IsSameDeviceImage) {
                NoteStandIn(frame: frame, index: index, producer: m_set.Instances[producer].Name);
            }
        }

        return reads;
    }
    // Adds each source producer's declaration this frame, unless the host or an upload declares that source already.
    private void AddProducerSourceStates() {
        for (var index = 0; (index < m_producers.Length); index++) {
            if (
                !m_set.Instances[index].IsSource ||
                (m_producers[index] is not IRenderGraphSourceProducer { Descriptor: { } descriptor })
            ) {
                continue;
            }

            var name = m_set.Instances[index].Name;
            var named = false;

            for (var position = 0; (position < m_sourceStates.Count); position++) {
                named |= string.Equals(
                    a: m_sourceStates[position].Instance,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                );
            }

            if (!named) {
                m_sourceStates.Add(item: new RenderGraphSourceState(
                    Cadence: descriptor.Cadence,
                    Height: ((int)descriptor.Height),
                    Instance: name,
                    Width: ((int)descriptor.Width)
                ));
            }
        }
    }
    // Whether any source instance renders through a producer that declares its own cadence and extent.
    private bool HasSourceProducers() {
        for (var index = 0; (index < m_producers.Length); index++) {
            if (
                m_set.Instances[index].IsSource &&
                (m_producers[index] is IRenderGraphSourceProducer)
            ) {
                return true;
            }
        }

        return false;
    }
}
