using Puck.Abstractions.Collections;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// Taint: an image holding external content the capture gate did not fill, and every output rendered from one. An
// external producer says so of each image it hands out; a graph instance's output is tainted when its render bound a
// tainted input. A capture frame renders every tainted instance the captured instance reads again, so a slow instance
// never carries external pixels into a capture, and a capture moves to its instance only over untainted inputs.
public sealed partial class RenderGraphRuntime {
    // The ordinals of instances a capture or standing recovery forces to render again, reused from frame to frame.
    private readonly MarkedIndexSet m_rerenderIndices = new();
    // The instances still to visit in the walk up from the captured instance, reused from frame to frame.
    private readonly Stack<int> m_unvisited = new();
    // The instances the walk up from the captured instance has reached, cleared sparsely.
    private readonly MarkedIndexSet m_visitedIndices = new();

    // Each external producer's delivery label at its latest acquisition, which is what its next consumer would bind.
    private DeliveryLabel[] m_producerLabels;
    // Whether the latest frame began with a capture pending anywhere in the runtime.
    private bool m_capturing;

    // Whether a read binds nothing this frame: on a capture frame, a previous-frame read of a tainted output. The capture
    // frame renders that producer again only after its reader, if at all, so binding it would carry the taint around a
    // loop of previous-frame reads (a camera view reading itself, two views reading each other) from frame to frame and
    // hold every capture it reaches. The one rule for both kinds of reader: a graph instance binds a stand-in in its place,
    // unnoted, and an external producer's read stays unbound.
    // The same holds for a read of an instance the frame does not render (one nothing shown reaches, such as a portal's
    // innermost level read only as a screen's source): its latest output stands from before the capture armed, the
    // capture frame cannot render it again, and binding it would hold every capture it reaches for as long as it stays
    // unshown, so on a capture frame its tainted output is withheld too.
    private bool Withholds(bool previousFrame, bool tainted, bool rendersThisFrame = true) => (
        m_capturing &&
        (previousFrame || !rendersThisFrame) &&
        tainted
    );
    // Whether the frame's schedule renders an instance.
    private static bool RendersIn(RenderGraphSchedule schedule, int producer) =>
        (schedule.Instances[producer] is { Status: RenderGraphInstanceStatus.Rendered });
    // Records that an instance's render bound a producer's tainted output.
    private void NoteTaint(int index, string producer, bool tainted) {
        if (tainted) {
            m_taintedReads[index] = producer;
        }
    }
    // A read's own taint, noted first, names the cause more nearly than the package state it went into, so it stays.
    private void NotePackageTaint(int index) {
        if ((m_taintedReads[index] is null) && (PackageTaintOf(index: index) is { } retained)) {
            NoteTaint(index: index, producer: retained, tainted: true);
        }
    }
    // Before rendering, retained taint guards capture forwarding. Only the successfully submitted package state
    // joins the new output's taint: a clean render may replace the previous tainted history in this same frame.
    private bool HasPackageTaint(int index) => (PackageTaintOf(index: index) is not null);
    // The retained package state of an instance that holds unfilled external content, named by the package that holds it
    // (IRenderGraphPackageFactory.TaintOf), or null when none does. It builds a string only while one does.
    private string? PackageTaintOf(int index) {
        var factories = m_instanceFactories[index];

        if (factories.Length == 0) { return null; }

        var name = m_set.Instances[index].Name;

        for (var position = 0; (position < factories.Length); position++) {
            if (factories[position].TaintOf(instance: name) is { } retained) {
                return $"{name}'s retained package state ({retained})";
            }
        }
        return null;
    }
    // Why a capture of an instance waits on taint, or null when it does not. Outside a capture frame a tainted instance is
    // expected; the capture frame renders it again, so only a taint that frame could not clear keeps a capture waiting.
    // The reason follows the tainted reads down to the state that holds the external content, so it names the cause.
    private string? TaintReasonOf(int index, string name) => ((m_capturing && ((m_taintedReads[index] ?? PackageTaintOf(index: index)) is { } tainting))
        ? $"the instance '{name}' has rendered only over external content from '{tainting}'{TaintSourceOf(producer: tainting)} that the capture gate did not fill"
        : null
    );
    // Where a tainting producer's own taint came from, followed through the instances that passed it on.
    private string TaintSourceOf(string producer) {
        var text = "";

        for (var (step, next) = (0, m_set.IndexOf(name: producer)); ((step < m_set.Instances.Count) && (next >= 0) && (m_taintedReads[next] is { } source)); step++) {
            text += $", which took it from '{source}'";
            next = m_set.IndexOf(name: source);
        }

        return text;
    }
    // Whether an instance's latest output is tainted: an external producer's as its latest acquisition said, a graph
    // instance's as its latest render bound.
    private bool IsTainted(int index) => ((m_producers[index] is not null)
        ? m_producerLabels[index].External
        : m_outputs.ExternalAt(index: index, latch: in m_latch)
    );
    // The instance whose node or producer currently holds a forwarded or directly armed capture, or -1 when none does.
    // Steady frames with no capture take O(1) (plus O(external producers)) instead of scanning every node.
    private int ForwardedCaptureIndex() {
        if (m_forwardedCaptureIndex is var forwarded and >= 0) {
            if ((((ICaptureRequestTarget?)m_nodes[forwarded]) ?? m_producers[forwarded])?.PendingCapturePath is not null) {
                return forwarded;
            }
            m_forwardedCaptureIndex = -1;
        }
        if (Volatile.Read(location: ref m_captureProbeNeeded) != 0) {
            _ = Interlocked.Exchange(location1: ref m_captureProbeNeeded, value: 0);
            for (var index = 0; (index < m_nodes.Length); index++) {
                if (m_nodes[index]?.PendingCapturePath is not null) {
                    m_forwardedCaptureIndex = index;

                    return index;
                }
            }
        }
        for (var position = 0; (position < m_externalProducerIndices.Length); position++) {
            var index = m_externalProducerIndices[position];

            if (m_producers[index]?.PendingCapturePath is not null) {
                m_forwardedCaptureIndex = index;

                return index;
            }
        }

        return -1;
    }
    // The instance the pending capture reads: the one armed on the runtime, or the instance whose node or producer holds
    // one; -1 when no capture is pending.
    private int CapturedInstance() => (((m_capture.PendingPath ?? PresentedPath) is not null)
        ? m_captureInstance
        : ForwardedCaptureIndex());
    // Collects the ordinals of every instance whose latest output stands for another's image to render again
    // (RenderGraphRuntime.Standing.cs), and, on a capture frame, every tainted or converging instance the captured
    // instance reads, itself included. A source is never named: its consumers resolve its image through the capture gate.
    private ReadOnlySpan<int> CollectRerenders(int captured) {
        var standing = CollectStanding();

        m_capturing = (captured >= 0);
        m_rerenderIndices.Clear();

        if (!m_capturing && !standing) {
            return [];
        }

        var standingMembers = m_standingIndices.Members;

        for (var position = 0; (position < standingMembers.Length); position++) {
            _ = m_rerenderIndices.Add(index: standingMembers[position]);
        }
        if (!m_capturing) {
            return m_rerenderIndices.Members;
        }

        m_visitedIndices.Clear();
        m_unvisited.Clear();
        m_unvisited.Push(item: captured);

        while (m_unvisited.TryPop(result: out var index)) {
            if (!m_visitedIndices.Add(index: index)) {
                continue;
            }

            DiscardCaptureTaint(index: index);

            if (
                !m_set.Instances[index].IsSource &&
                (IsTainted(index: index) || IsConverging(index: index))
            ) {
                _ = m_rerenderIndices.Add(index: index);
            }

            var reads = m_set.Reads[index];

            for (var read = 0; (read < reads.Count); read++) {
                m_unvisited.Push(item: reads[read].Producer);
            }
        }

        return m_rerenderIndices.Members;
    }
}
