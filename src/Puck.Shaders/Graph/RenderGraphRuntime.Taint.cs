using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// Taint: an image holding external content the capture gate did not fill, and every output rendered from one. An
// external producer says so of each image it hands out; a graph instance's output is tainted when its render bound a
// tainted input. A capture frame renders every tainted instance the captured instance reads again, so a slow instance
// never carries external pixels into a capture, and a capture moves to its instance only over untainted inputs.
public sealed partial class RenderGraphRuntime {
    // The instances a capture frame renders again, reused from frame to frame.
    private readonly List<string> m_rerender = [];
    // The instances still to visit in the walk up from the captured instance, reused from frame to frame.
    private readonly Stack<int> m_unvisited = new();

    // Each external producer's taint at its latest acquisition, which is what its next consumer would bind.
    private bool[] m_producerTainted;
    // Whether the latest frame began with a capture pending anywhere in the runtime.
    private bool m_capturing;

    // The instances the walk up from the captured instance has reached, sized to the set.
    private bool[] m_visited = [];

    // Whether a read binds nothing this frame: on a capture frame, a previous-frame read of a tainted output. The capture
    // frame renders that producer again only after its reader, if at all, so binding it would carry the taint around a
    // loop of previous-frame reads (a camera view reading itself, two views reading each other) from frame to frame and
    // hold every capture it reaches. The one rule for both kinds of reader: a graph instance binds a stand-in in its place,
    // unnoted, and an external producer's read stays unbound.
    private bool Withholds(bool previousFrame, bool tainted) => (
        m_capturing &&
        previousFrame &&
        tainted
    );
    // Records that an instance's render bound a producer's tainted output.
    private void NoteTaint(int index, string producer, bool tainted) {
        if (tainted) {
            m_taintedReads[index] = producer;
        }
    }
    // Why a capture of an instance waits on taint, or null when it does not. Outside a capture frame a tainted instance is
    // expected; the capture frame renders it again, so only a taint that frame could not clear keeps a capture waiting.
    private string? TaintReasonOf(int index, string name) => ((m_capturing && (m_taintedReads[index] is { } tainting))
        ? $"the instance '{name}' has rendered only over external content from '{tainting}' that the capture gate did not fill"
        : null
    );
    // Whether an instance's latest output is tainted: an external producer's as its latest acquisition said, a graph
    // instance's as its latest render bound.
    private bool IsTainted(int index) => ((m_producers[index] is not null)
        ? m_producerTainted[index]
        : m_current[index].Tainted
    );
    // The instance the pending capture reads: the one armed on the runtime, or the instance whose node or producer holds
    // one; -1 when no capture is pending.
    private int CapturedInstance() {
        if (m_capture.PendingPath is not null) {
            return m_captureInstance;
        }

        for (var index = 0; (index < m_nodes.Length); index++) {
            if ((((ICaptureRequestTarget?)m_nodes[index]) ?? m_producers[index])?.PendingCapturePath is not null) {
                return index;
            }
        }

        return -1;
    }
    // The frame the scheduler reads: on a capture frame, the frame naming every tainted instance the captured instance
    // reads, itself included, to render again; any other frame as given. A source is never named: its consumers resolve
    // its image through the capture gate as they bind it.
    private RenderGraphFrame WithRerenders(in RenderGraphFrame frame) {
        var captured = CapturedInstance();

        m_capturing = (captured >= 0);

        if (!m_capturing) {
            return frame;
        }

        var count = m_set.Instances.Count;

        if (m_visited.Length != count) {
            m_visited = new bool[count];
        } else {
            Array.Clear(array: m_visited);
        }

        m_rerender.Clear();

        if (frame.Rerender is { } declared) {
            m_rerender.AddRange(collection: declared);
        }

        m_unvisited.Clear();
        m_unvisited.Push(item: captured);

        while (m_unvisited.TryPop(result: out var index)) {
            if (m_visited[index]) {
                continue;
            }

            m_visited[index] = true;

            var instance = m_set.Instances[index];

            if (
                !instance.IsSource &&
                IsTainted(index: index) &&
                !m_rerender.Contains(item: instance.Name)
            ) {
                m_rerender.Add(item: instance.Name);
            }

            var reads = m_set.Reads[index];

            for (var read = 0; (read < reads.Count); read++) {
                m_unvisited.Push(item: reads[read].Producer);
            }
        }

        return (frame with {
            Rerender = m_rerender,
        });
    }
}
