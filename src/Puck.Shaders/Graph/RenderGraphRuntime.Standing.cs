using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// An output standing for another instance's image. A package pass that draws nothing publishes its input in its output's
// place (RenderGraphPackageOutcome.DrewNothing), so an instance's output may be an image another instance owns. Such an
// output is never a copy of that image's handle: it records whose output it stands for, and every read resolves it
// (Resolve).
//
// - An output standing for a graph instance's output is that producer's output: read at a frame, it is the producer's
//   newest output no newer than that frame, or than the frame before it when the instance read the producer's previous
//   frame, as a read of the producer would be. A drawn-nothing output equals its input, so it follows the producer at
//   the producer's cadence and its own instance keeps its refresh: nothing renders again to keep it current. Once the
//   producer is released because nothing names it, or retires in a reconfiguration, the output resolves to nothing.
// - An output standing for an image bound for one frame only, an external producer's leased output or a binding a
//   retired producer's hold keeps, resolves within that frame and to nothing after it.
//
// Feedback never stands for itself: a node refuses to stand for an image it owns (ShaderPipelineRenderNode
// .OwnImageInput), since it renders into its own images again a few frames later, so an instance reading its own
// previous frame, or a loop of instances reading each other, draws where it would close the loop. Every chain therefore
// ends at another instance's own output, which the image leases keep alive for each reader that bound it
// (RenderGraphRuntime.Leases.cs).
//
// A reader of an output that resolves to nothing binds a stand-in. So that the display never shows nothing in its place,
// the instance whose output resolves to nothing, or would after this frame, renders again whenever something shows it:
// CollectRerenders names it, and a frame that releases an instance another's output stood for is scheduled again
// (RescheduleAfterRelease), so the reader binds the released producer's stand-in in the same frame. Only an output
// standing for a one-frame image costs a render every frame it is shown.
//
// Every record carries the label of what it was rendered from (DeliveryLabel), and the records are private to
// OutputRecords, whose reads answer nothing for a record whose label the frame's latch refuses. A resolved output
// carries the union of every hop's label, so an output standing for an ended producer resolves to nothing too.
public sealed partial class RenderGraphRuntime {
    // The frame outputs are read at, retained when a reconfiguration replaces the scheduling history.
    private long m_readFrame = -1;

    // The ordinals of instances whose latest output must render again to stay shown, reused from frame to frame.
    private readonly MarkedIndexSet m_standingIndices = new();
    // Sparse candidates whose current output stands for another image or carries a span that can end (tracked on write,
    // lazily pruned once it is neither).
    private readonly List<int> m_standingCandidates = [];
    private bool[] m_standingCandidateMark = [];
    // The instances the frame's release forgot, reused from frame to frame.
    private readonly List<int> m_released = [];

    // The completed output records: every read passes a DeliveryLatch, and RememberOutput is the one writer of a committed
    // record. A frame's reservation (ReserveOutput) is written ahead of its commit, which replaces it in place, or is
    // withdrawn, which puts back the records it displaced; no reservation outlives its frame.
    private sealed class OutputRecords {
        private Output[] m_current;
        private Output[] m_previous;
        // The record a reservation pushed out of m_previous, which its withdrawal puts back.
        private Output[] m_displaced;

        public OutputRecords(int count) {
            m_current = new Output[count];
            m_previous = new Output[count];
            m_displaced = new Output[count];
            Clear();
        }

        public bool IsReserved(int index) => m_current[index].Reserved;
        public Output Reservation(int index) => m_current[index];
        public void Reserve(int index, ShaderPipelineRenderNode node, long frame, in Surface surface, GpuImageLayout layout, DeliveryLabel label, Standing standsFor, ReferenceRevision publication) {
            var buffer = node.ReservedOutputBuffer();
            var buffers = node.ReservedOutputBuffers(reuse: m_displaced[index].Buffers);

            m_displaced[index] = m_previous[index];
            m_previous[index] = m_current[index];
            m_current[index] = new Output(
                Buffer: buffer.Buffer,
                BufferPublication: buffer.Publication,
                Buffers: buffers,
                Frame: frame,
                Image: surface,
                Label: label,
                Layout: layout,
                Publication: publication,
                Reserved: true,
                StandsFor: standsFor,
                StateTick: node.ReservedStateTick
            );
        }
        // Puts back the records a reservation displaced, as if the frame had never been prepared.
        public void Withdraw(int index) {
            if (!m_current[index].Reserved) { return; }
            m_current[index] = m_previous[index];
            m_previous[index] = m_displaced[index];
            m_displaced[index] = Output.None;
        }

        public Output ReadAt(int producer, long frame, in DeliveryLatch latch) {
            var current = m_current[producer];

            if (current.StandsFor.IsRetired) {
                return Output.None;
            }
            if (
                (current.Frame >= 0) &&
                (current.Frame <= frame)
            ) {
                return (current.Label.Allows(latch: in latch)
                    ? current
                    : Output.None);
            }

            var previous = m_previous[producer];

            return (((previous.Frame >= 0) && (previous.Frame <= frame) && !previous.StandsFor.IsRetired && previous.Label.Allows(latch: in latch))
                ? previous
                : Output.None);
        }
        public Output ReadCurrent(int index, in DeliveryLatch latch) {
            var current = m_current[index];

            return (((current.Frame >= 0) && !current.StandsFor.IsRetired && current.Label.Allows(latch: in latch))
                ? current
                : Output.None);
        }
        public void Write(int index, ShaderPipelineRenderNode node, long frame, in Surface surface, DeliveryLabel label, Standing standsFor, ReferenceRevision publication) {
            var buffer = node.LatestOutputBuffer();
            // A commit replaces its own reservation, whose consumers compared what they bound before this write.
            var reserved = m_current[index].Reserved;
            var buffers = node.LatestOutputBuffers(reuse: (reserved ? m_current[index].Buffers : m_previous[index].Buffers));

            if (reserved) {
                m_displaced[index] = Output.None;
            } else {
                m_previous[index] = m_current[index];
            }
            m_current[index] = new Output(
                Buffer: buffer.Buffer,
                BufferPublication: buffer.Publication,
                Buffers: buffers,
                Frame: frame,
                Image: surface,
                Label: label,
                Layout: node.PublishedLayout,
                Publication: publication,
                StandsFor: standsFor,
                StateTick: node.PublishedStateTick
            );
        }
        public void Forget(int index) {
            m_current[index] = Output.None;
            m_previous[index] = Output.None;
            m_displaced[index] = Output.None;
        }
        public void Clear() {
            Array.Fill(array: m_current, value: Output.None);
            Array.Fill(array: m_previous, value: Output.None);
            Array.Fill(array: m_displaced, value: Output.None);
        }
        public void Reconfigure(int count, ReadOnlySpan<int> kept, int[] renumbered) {
            var current = new Output[count];
            var previous = new Output[count];

            m_displaced = new Output[count];
            Array.Fill(array: m_displaced, value: Output.None);

            for (var index = 0; (index < count); index++) {
                var old = kept[index];

                current[index] = ((old >= 0)
                    ? Renumbered(output: in m_current[old], renumbered: renumbered)
                    : Output.None);
                previous[index] = ((old >= 0)
                    ? Renumbered(output: in m_previous[old], renumbered: renumbered)
                    : Output.None);
            }

            m_current = current;
            m_previous = previous;
        }
        public int Count => m_current.Length;
        // What the current record stands for, which says nothing of its content.
        public Standing StandsFor(int index) => m_current[index].StandsFor;
        // The producer the current record stands for, or -1 when it stands for none or the latch refuses it.
        public int StandsForProducer(int index, in DeliveryLatch latch) =>
            (m_current[index].Label.Allows(latch: in latch)
                ? m_current[index].StandsFor.Producer
                : -1);
        // Whether the current record must stay a standing candidate: it stands for another image, or its label names a
        // span that can end.
        public bool IsStandingCandidate(int index) => (
            !m_current[index].StandsFor.IsOwn ||
            ((m_current[index].Frame >= 0) && (m_current[index].Label.SpanCount != 0))
        );
        public bool HasEnded(int index, in DeliveryLatch latch) =>
            ((m_current[index].Frame >= 0) && !m_current[index].Label.Allows(latch: in latch));
        public bool ExternalAt(int index, in DeliveryLatch latch) =>
            (m_current[index].Label.Allows(latch: in latch) && m_current[index].Label.External);
    }

    private void TrackStandingCandidate(int index) {
        if (!m_standingCandidateMark[index]) {
            m_standingCandidateMark[index] = true;
            m_standingCandidates.Add(item: index);
        }
    }
    private void RebuildStandingCandidates() {
        m_standingCandidates.Clear();
        m_standingCandidateMark = new bool[m_outputs.Count];

        for (var index = 0; (index < m_outputs.Count); index++) {
            if (m_outputs.IsStandingCandidate(index: index)) {
                TrackStandingCandidate(index: index);
            }
        }
    }
    private void ClearStandingCandidates() {
        for (var position = 0; (position < m_standingCandidates.Count); position++) {
            m_standingCandidateMark[m_standingCandidates[position]] = false;
        }
        m_standingCandidates.Clear();
    }

    // What an output's image is when it is not the instance's own: another graph instance's output (Producer at least
    // zero), at the frame read or, for a previous-frame read, the frame before it; an image bound for one frame only
    // (Producer -1, Frame that frame); or an image of an instance that retired in a reconfiguration (Producer -2), which
    // resolves to nothing. A stand-in is the runtime's own and lives until the device is lost or the runtime disposed,
    // which clear every output, so an output publishing one is the instance's own.
    private readonly record struct Standing(int Producer, long Frame, bool PreviousFrame = false) {
        public static Standing Own => new(
            Frame: -1L,
            Producer: -1
        );
        public static Standing Retired => new(
            Frame: -1L,
            Producer: -2
        );
        public bool IsOwn => ((Producer == -1) && (Frame < 0L));
        public bool IsOneFrame => ((Producer == -1) && (Frame >= 0L));
        public bool IsRetired => (Producer == -2);
    }

    // The newest completed output of a producer that is no newer than a frame and allowed at the latch, as recorded.
    private Output RecordedAt(int producer, long frame, in DeliveryLatch latch) =>
        m_outputs.ReadAt(frame: frame, latch: in latch, producer: producer);
    // An output as a reader at a frame sees it: an instance's own output as it is; one standing for a producer's output,
    // that producer's output no newer than the frame (the frame before it for a previous-frame read), resolved in turn
    // with every standing hop's label unioned; one standing for a one-frame image, itself within that frame. Nothing
    // when what it stands for is gone or refused at the latch. A node never stands for its own image, so a chain ends at
    // another instance's own output; the bound only guards the walk. A consumer binding the result names itself, so each
    // reservation the walk passes through is noted as one it depends on (NoteReservedRead).
    private Output Resolve(in Output output, long frame, in DeliveryLatch latch, int consumer = -1) {
        if (output.Frame < 0) {
            return Output.None;
        }

        var resolved = output;
        var combinedLabel = output.Label;

        for (var depth = 0; (depth <= (2 * m_nodes.Length)); depth++) {
            var standing = resolved.StandsFor;

            if (standing.IsOwn) {
                return (resolved with { Label = combinedLabel });
            }
            if (standing.IsOneFrame) {
                return ((standing.Frame == m_latest?.Frame)
                    ? (resolved with { Label = combinedLabel })
                    : Output.None);
            }
            if (standing.IsRetired) {
                return Output.None;
            }

            frame -= (standing.PreviousFrame ? 1L : 0L);
            resolved = RecordedAt(
                frame: frame,
                latch: in latch,
                producer: standing.Producer
            );
            if (resolved.Frame < 0) {
                return Output.None;
            }
            if (resolved.Reserved && (consumer >= 0)) {
                NoteReservedRead(consumer: consumer, producer: standing.Producer);
            }
            combinedLabel = combinedLabel.Union(other: resolved.Label);
        }

        return Output.None;
    }
    // An instance's latest output as the display, a host or a capture sees it at the latch.
    private Output LatestOf(int index, in DeliveryLatch latch) => Resolve(
        frame: m_readFrame,
        latch: in latch,
        output: m_outputs.ReadCurrent(index: index, latch: in latch)
    );
    // What an instance's new output stands for: nothing when its node published an image of its own, otherwise the
    // producer of the image this frame bound to the external version its node published in its output's place. A stand-in
    // bound there is the runtime's own; an external producer's leased image, or a name the instance's inputs no longer
    // bind (a retired producer's hold), is bound for this frame only.
    private Standing StandingOf(int index, string? forwarded, RenderGraphSchedule schedule, in Surface surface) {
        if (forwarded is not { } version) {
            return Standing.Own;
        }

        foreach (var binding in m_inputs[index]) {
            if (
                !string.Equals(
                    a: binding.Version,
                    b: version,
                    comparisonType: StringComparison.Ordinal
                ) ||
                (m_producers[binding.Producer] is not null)
            ) {
                continue;
            }

            var bound = OutputAt(
                frame: schedule.ReadFrame(
                    consumer: index,
                    producer: binding.Producer
                ),
                latch: in m_latch,
                producer: binding.Producer,
                readFrame: (schedule.Frame - (binding.PreviousFrame ? 1L : 0L))
            );

            return (((bound.Frame >= 0L) && (bound.Image.ImageHandle == surface.ImageHandle))
                ? new Standing(
                    Frame: -1L,
                    PreviousFrame: binding.PreviousFrame,
                    Producer: binding.Producer
                )
                : Standing.Own);
        }

        return new Standing(
            Frame: schedule.Frame,
            Producer: -1
        );
    }
    // Collects every instance whose latest output must render again to stay shown, returning whether there is any: its
    // outputs were withdrawn (RenderGraphRuntime.Withdrawal.cs), its label names a span the frame's latch refuses, it
    // resolves to nothing, or it stands for a one-frame image, which resolves to nothing once the frame it was bound in
    // has passed. Only the sparse standing candidates are visited, and withdrawing instances only while there are any.
    private bool CollectStanding() {
        m_standingIndices.Clear();

        for (var position = (m_standingCandidates.Count - 1); (position >= 0); position--) {
            var index = m_standingCandidates[position];
            var standing = m_outputs.StandsFor(index: index);

            if (!m_outputs.IsStandingCandidate(index: index)) {
                m_standingCandidateMark[index] = false;
                var last = (m_standingCandidates.Count - 1);

                m_standingCandidates[position] = m_standingCandidates[last];
                m_standingCandidates.RemoveAt(index: last);

                continue;
            }
            if (
                (m_sources[index] is null) &&
                (
                    // A render under a route the latch refuses would end at once too, so it is not asked for.
                    (m_outputs.HasEnded(index: index, latch: in m_latch) && m_routes[index].Allows(latch: in m_latch)) ||
                    (
                        !standing.IsOwn &&
                        (standing.IsOneFrame || (LatestOf(index: index, latch: in m_latch).Frame < 0))
                    )
                )
            ) {
                _ = m_standingIndices.Add(index: index);
            }
        }
        if (m_withdrawing.Count != 0) {
            for (var index = 0; (index < m_nodes.Length); index++) {
                if ((m_sources[index] is null) && IsWithdrawing(index: index)) {
                    _ = m_standingIndices.Add(index: index);
                }
            }
        }

        return (m_standingIndices.Count != 0);
    }
    // Schedules a frame again after its release left an output standing for a released instance, naming that output's
    // instance to render again, so the display and every reader bind the released producer's stand-in this frame rather
    // than an empty image. The release does not depend on what renders, so the second schedule releases nothing more; the
    // released instances are forgotten again in its history.
    private void RescheduleAfterRelease(RenderGraphSchedule schedule, RenderGraphHistory prior, in RenderGraphFrame frame, in RenderGraphOrdinalOverlay overlay) {
        if (
            (m_released.Count == 0) ||
            !CollectStanding()
        ) {
            return;
        }

        var standingMembers = m_standingIndices.Members;

        for (var position = 0; (position < standingMembers.Length); position++) {
            _ = m_rerenderIndices.Add(index: standingMembers[position]);
        }

        var updated = new RenderGraphOrdinalOverlay {
            Rerender = m_rerenderIndices.Members,
            Sources = overlay.Sources,
            Stale = overlay.Stale,
            Unchanged = overlay.Unchanged,
        };

        RenderGraphScheduler.ScheduleOrdinal(
            frame: frame,
            history: prior,
            overlay: in updated,
            schedule: schedule,
            set: m_set
        );

        foreach (var released in m_released) {
            schedule.Next.Forget(index: released);
        }
    }
    // A kept instance's output in a reconfigured set: what it stands for renumbered into the new set, or, when its
    // producer retires, an output standing for a retired image, which resolves to nothing and is never the instance's own,
    // so no reader binds it and no capture moves to the node still publishing it.
    private static Output Renumbered(in Output output, int[] renumbered) {
        var standing = output.StandsFor;

        if (standing.Producer < 0) {
            return output;
        }

        var producer = renumbered[standing.Producer];

        return ((producer < 0)
            ? (Output.None with {
                StandsFor = Standing.Retired,
            })
            : (output with {
                StandsFor = (standing with {
                    Producer = producer,
                }),
            }));
    }
}
