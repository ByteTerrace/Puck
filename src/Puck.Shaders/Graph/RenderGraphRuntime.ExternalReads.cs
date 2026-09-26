using Puck.Hosting;

namespace Puck.Shaders;

// The images an external producer reads. An external instance may read other instances' images within the frame (the
// set refuses its buffer and previous-frame reads): before it produces, the runtime binds each read to the latest
// completed output of the instance read, an external producer's under the lease its acquisition returns and a graph
// instance's unleased, and hands the list to Produce. The producer takes the leases its submission samples; the rest are
// retired once Produce returns.
public sealed partial class RenderGraphRuntime {
    // Each external instance's reads, or null for one that reads nothing, for the set they were made for.
    private RenderGraphExternalReads?[] m_externalReads = [];
    private RenderGraphInstanceSet? m_externalReadsSet;

    // Binds an external instance's reads for the frame, or returns null when it reads nothing.
    private RenderGraphExternalReads? BindExternalReads(int index, RenderGraphSchedule schedule) {
        if (!ReferenceEquals(
            objA: m_externalReadsSet,
            objB: m_set
        )) {
            m_externalReads = new RenderGraphExternalReads?[m_set.Instances.Count];
            m_externalReadsSet = m_set;
        }

        var edges = m_set.Reads[index];

        if (edges.Count == 0) {
            return null;
        }

        var reads = (m_externalReads[index] ??= new RenderGraphExternalReads(producers: [
            .. edges.Select(selector: edge => m_set.Instances[edge.Producer].Name),
        ]));
        var consumer = m_set.Instances[index].Name;

        for (var position = 0; (position < edges.Count); position++) {
            var producer = edges[position].Producer;

            if (m_producers[producer] is { } external) {
                if (external.TryAcquireOutput(output: out var output)) {
                    reads.Bind(
                        image: output.Image,
                        index: position,
                        layout: output.Layout,
                        lease: output.Lease
                    );
                }

                continue;
            }

            var frame = FrameOf(
                consumer: consumer,
                producer: m_set.Instances[producer].Name,
                schedule: schedule
            );
            // A read the frame does not show still binds the producer's latest output, so an image the display will
            // show again is never replaced by nothing.
            var completed = OutputAt(
                frame: ((frame < 0)
                    ? long.MaxValue
                    : frame),
                producer: producer
            );

            if (completed.Image.IsSameDeviceImage) {
                reads.Bind(
                    image: completed.Image,
                    index: position,
                    layout: completed.Layout,
                    lease: completed.Image.ImageViewHandle
                );
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
