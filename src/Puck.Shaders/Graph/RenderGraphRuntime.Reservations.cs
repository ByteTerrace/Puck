using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Reservations: a graph node whose prepared frame will record writes, as soon as it is prepared, the output its commit
// will publish (ReserveOutput), so every consumer of the same frame binds it while being prepared and every recording of
// the frame runs in one fork. A consumer's command list samples a reserved image only because the producer submits first:
// the commit runs in schedule order, and a producer precedes its consumers in it.
//
// Invariant: no command list reads a resource whose producer has not submitted, in the same frame, exactly what the
// consumer bound, unless it reads a stand-in or an earlier frame's output. One door enforces it, the commit loop
// (RenderGraphRuntime.Waves.cs): each consumer is committed only if every reservation it bound held (HoldsReservations), and
// otherwise abandoned, so nothing it recorded reaches the queue, its own reservation is withdrawn, and its consumers, later
// in the same loop, are abandoned in turn: the dependency closure. An abandoned instance keeps its last good output, which
// is what its readers bind on the next frame. Every read of another instance's output for a binding passes its consumer to
// OutputAt or Resolve, or notes the read itself, so a new binding path that reads a reservation without naming its consumer
// is the one way around the door; review holds that.
//
// What breaks a reservation (SettleReservation): a commit that did not happen (a failed or abandoned recording), one that
// published another image, layout, buffer, forwarded binding or image publication (a stand-in outcome that flipped), and one
// whose label gained external content the reservation did not carry. A buffer's content identity is predicted standing and
// may change if the pass records; only a consumer whose pass stood on that identity depends on it, so such a consumer alone
// is abandoned for it.
public sealed partial class RenderGraphRuntime {
    [Flags]
    private enum ReservationBreak : byte {
        None = 0,
        // The frame did not commit: its recording failed or was abandoned, or its output was forgotten.
        Failed = 1,
        // The commit published another image, layout, buffer, forwarded binding or image publication.
        Image = 2,
        // The commit's label carries external content the reservation did not.
        Taint = 4,
    }
    // One reservation a consumer bound: its producer and, for a buffer, the version it bound it to, the producer's output it
    // reads (null for the default) and what the reservation said of it.
    private readonly record struct ReservedRead(int Producer, string? Version = null, string? Output = null, IGpuBuffer? Buffer = null, ReferenceRevision Publication = default);

    // Per instance, the reservations its current frame bound, cleared as it begins.
    private List<ReservedRead>?[] m_reservedReads = [];
    // Per instance, how its reservation broke this frame, set as it settles.
    private ReservationBreak[] m_reservationBreaks = [];

    private void SizeReservations(int count) {
        if (m_reservedReads.Length != count) {
            m_reservedReads = new List<ReservedRead>?[count];
            m_reservationBreaks = new ReservationBreak[count];
        }
    }
    private void BeginReads(int index) {
        SizeReservations(count: m_nodes.Length);
        m_reservedReads[index]?.Clear();
        m_reservationBreaks[index] = ReservationBreak.None;
    }
    // Notes an image reservation the consumer bound, through a binding or a standing hop.
    private void NoteReservedRead(int consumer, int producer) => (m_reservedReads[consumer] ??= []).Add(item: new ReservedRead(Producer: producer));
    // Notes a buffer reservation the consumer bound to a version.
    private void NoteReservedRead(int consumer, int producer, string version, string? output, IGpuBuffer buffer, ReferenceRevision publication) =>
        (m_reservedReads[consumer] ??= []).Add(item: new ReservedRead(Buffer: buffer, Output: output, Producer: producer, Publication: publication, Version: version));

    // Writes the prepared frame's reservation, which the node computed by the routines its commit publishes by.
    private void ReserveOutput(int index, ShaderPipelineRenderNode node, RenderGraphSchedule schedule) {
        node.ReserveOutputs();

        var label = m_routes[index].Union(other: node.ReservedLabel);

        // Retained package taint can only clear in this frame's render, so the reservation carries it until the commit.
        if ((m_taintedReads[index] is not null) || HasPackageTaint(index: index)) {
            label = label.WithExternal(external: true);
        }
        // A frame that publishes nothing classified leaves no record; its consumers bind the last one.
        if (!label.IsClassified) { return; }

        var surface = node.ReservedSurface;

        m_outputs.Reserve(
            frame: schedule.Frame,
            index: index,
            label: label,
            layout: node.ReservedLayout,
            node: node,
            publication: node.PublicationOf(binding: node.ReservedBinding, latch: in m_latch, revision: node.ReservedSubmission),
            standsFor: StandingOf(forwarded: node.ReservedBinding, index: index, schedule: schedule, surface: in surface),
            surface: in surface
        );
    }
    // Settles a reservation as its frame commits or is given up: compares what the commit publishes with what was reserved
    // and records how it broke, then lets RememberOutput replace it (rendered) or puts back the records it displaced.
    // Returns the reservation as it was, for the comparison RememberOutput's record completes (FinishReservation).
    private Output SettleReservation(int index, ShaderPipelineRenderNode node, bool rendered) {
        if (!m_outputs.IsReserved(index: index)) { return Output.None; }

        var reserved = m_outputs.Reservation(index: index);

        if (!rendered) {
            m_outputs.Withdraw(index: index);
            m_reservationBreaks[index] = ReservationBreak.Failed;
            return Output.None;
        }

        var published = node.PublishedOutputs;
        var buffers = (reserved.Buffers ?? []);
        var broke = ReservationBreak.None;

        if (buffers.Length != published.Length) {
            broke |= ReservationBreak.Image;
        } else {
            for (var at = 0; (at < buffers.Length); at++) {
                if (!ReferenceEquals(objA: buffers[at].Buffer, objB: published[at].Buffer) ||
                    (buffers[at].OwnedImageHandle != published[at].OwnedImageHandle) ||
                    !SameImage(first: buffers[at].Image, second: published[at].Image)) {
                    broke |= ReservationBreak.Image;
                }
            }
        }
        m_reservationBreaks[index] = broke;
        return reserved;
    }
    // Whether two published images name the same image and view in the same layout at the frame's latch.
    private bool SameImage(in ShaderPipelineExternalImage first, in ShaderPipelineExternalImage second) {
        var firstRead = first.TryRead(latch: in m_latch, image: out var a);
        var secondRead = second.TryRead(latch: in m_latch, image: out var b);

        return ((firstRead == secondRead) &&
            (!firstRead || ((a.ImageHandle == b.ImageHandle) && (a.ImageViewHandle == b.ImageViewHandle) && (a.Layout == b.Layout))));
    }
    // Completes the comparison once RememberOutput has written the commit's record over the reservation.
    private void FinishReservation(int index, in Output reserved, long frame) {
        if (reserved.Frame < 0) { return; }

        var actual = m_outputs.Reservation(index: index);

        if ((actual.Frame != frame) || actual.Reserved) {
            // RememberOutput forgot the record (an unclassified label) or never replaced the reservation.
            m_outputs.Withdraw(index: index);
            m_reservationBreaks[index] |= ReservationBreak.Failed;
            return;
        }
        if ((actual.Image.ImageHandle != reserved.Image.ImageHandle) || (actual.Image.ImageViewHandle != reserved.Image.ImageViewHandle) ||
            (actual.Layout != reserved.Layout) || (actual.StandsFor != reserved.StandsFor) || (actual.Publication != reserved.Publication) ||
            !ReferenceEquals(objA: actual.Buffer, objB: reserved.Buffer)) {
            m_reservationBreaks[index] |= ReservationBreak.Image;
        }
        if (actual.Label.External && !reserved.Label.External) {
            m_reservationBreaks[index] |= ReservationBreak.Taint;
        }
    }
    // Whether every reservation an instance's prepared frame bound held, so its recording may reach the queue; otherwise
    // names a producer whose reservation broke.
    private bool HoldsReservations(int index, out int broken) {
        broken = -1;
        if ((m_reservedReads.Length <= index) || (m_reservedReads[index] is not { Count: > 0 } reads)) { return true; }

        var node = m_nodes[index]!;

        foreach (var read in reads) {
            var producer = read.Producer;

            if (m_reservationBreaks[producer] != ReservationBreak.None) {
                broken = producer;
                return false;
            }
            if (read.Version is not { } version) { continue; }

            // A buffer whose content identity changed while the consumer's pass stood on the reserved one.
            var actual = m_outputs.ReadCurrent(index: producer, latch: in m_latch).BufferOf(name: read.Output, publication: out var publication);

            if (!ReferenceEquals(objA: actual, objB: read.Buffer) || ((publication != read.Publication) && node.StoodOnExternal(version: version))) {
                broken = producer;
                return false;
            }
        }
        return true;
    }
    // Gives up a prepared frame that bound a reservation which did not hold: nothing it recorded reaches the queue, its own
    // reservation is withdrawn, and it keeps its last good output.
    private void InvalidateRender(ref FrameRenders rendering, in WaveJob job, int broken) {
        AbandonRender(rendering: ref rendering, job: in job);
        m_unproduced++;
        RenderRecordWork.Process.Invalidated();
        MarkStale(index: job.Index, reason: $"the instance '{m_set.Instances[job.Index].Name}' read '{m_set.Instances[broken].Name}', whose frame did not commit as it was prepared");
    }
}
