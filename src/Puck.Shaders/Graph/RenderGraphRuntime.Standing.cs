using Puck.Abstractions.Collections;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// An output standing for another instance's image. A package pass that draws nothing publishes its input in its output's
// place (RenderGraphPackageOutcome.DrewNothing), so an instance's output may be an image another instance owns. Such an
// output is never a copy of that image's handle: it records whose output it stands for, and every read resolves it
// (Resolve) through OutputRecords, whose private arrays enforce that every output read passes the frame's latch.
public sealed partial class RenderGraphRuntime {
    // The frame outputs are read at, retained when a reconfiguration replaces the scheduling history.
    private long m_readFrame = -1;

    // The instances whose latest output resolves to nothing, or stands for a one-frame image, reused from frame to frame.
    private readonly List<string> m_standing = [];
    // The instances the frame's release forgot, reused from frame to frame.
    private readonly List<int> m_released = [];

    // Owns the completed output records so no caller outside this type can index m_current or m_previous without a
    // DeliveryLatch, and only RememberOutput writes new records.
    private sealed class OutputRecords {
        private Output[] m_current;
        private Output[] m_previous;

        public OutputRecords(int count) {
            m_current = new Output[count];
            m_previous = new Output[count];
            Clear();
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
            var buffers = node.LatestOutputBuffers(reuse: m_previous[index].Buffers);

            m_previous[index] = m_current[index];
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
        }
        public void Clear() {
            Array.Fill(array: m_current, value: Output.None);
            Array.Fill(array: m_previous, value: Output.None);
        }
        public void Reconfigure(int count, ReadOnlySpan<int> kept, int[] renumbered) {
            var current = new Output[count];
            var previous = new Output[count];

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
        public bool StandsForRetired(int index) => m_current[index].StandsFor.IsRetired;
        public bool StandsForOwn(int index) => m_current[index].StandsFor.IsOwn;
        public bool StandsForOneFrame(int index) => m_current[index].StandsFor.IsOneFrame;
        public int StandsForProducer(int index, in DeliveryLatch latch) =>
            (m_current[index].Label.Allows(latch: in latch)
                ? m_current[index].StandsFor.Producer
                : -1);
        public bool HasEnded(int index, in DeliveryLatch latch) =>
            ((m_current[index].Frame >= 0) && !m_current[index].Label.Allows(latch: in latch));
        public bool ExternalAt(int index, in DeliveryLatch latch) =>
            (m_current[index].Label.Allows(latch: in latch) && m_current[index].Label.External);
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
    // when what it stands for is gone or disallowed at the latch.
    private Output Resolve(in Output output, long frame, in DeliveryLatch latch) {
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
    // producer of the image this frame bound to the external version its node published in its output's place.
    private Standing StandingOf(int index, ShaderPipelineRenderNode node, RenderGraphSchedule schedule, in Surface surface) {
        if (node.PublishedBinding is not { } version) {
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
                frame: FrameOf(
                    consumer: m_set.Instances[index].Name,
                    producer: binding.ProducerName,
                    schedule: schedule
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
    // Whether an instance's latest output must render again to stay shown: its outputs were withdrawn, its recorded
    // output's span ended at the latch, it resolves to nothing, or it stands for a one-frame image.
    private bool RendersToStayShown(int index) => (
        (m_sources[index] is null) &&
        (
            IsWithdrawing(index: index) ||
            m_outputs.HasEnded(index: index, latch: in m_latch) ||
            (
                !m_outputs.StandsForOwn(index: index) &&
                (m_outputs.StandsForOneFrame(index: index) || (LatestOf(index: index, latch: in m_latch).Frame < 0))
            )
        )
    );
    // Collects every instance whose latest output must render again to stay shown, returning whether there is any.
    private bool CollectStanding() {
        m_standing.Clear();

        for (var index = 0; (index < m_nodes.Length); index++) {
            if (RendersToStayShown(index: index)) {
                m_standing.Add(item: m_set.Instances[index].Name);
            }
        }

        return (m_standing.Count != 0);
    }
    // Schedules a frame again after its release left an output standing for a released instance, naming that output's
    // instance to render again, so the display and every reader bind the released producer's stand-in this frame rather
    // than an empty image.
    private void RescheduleAfterRelease(RenderGraphSchedule schedule, RenderGraphHistory prior, in RenderGraphFrame frame) {
        if (
            (m_released.Count == 0) ||
            !CollectStanding()
        ) {
            return;
        }

        if (!ReferenceEquals(
            objA: frame.Rerender,
            objB: m_rerender
        )) {
            m_rerender.Clear();

            if (frame.Rerender is { } declared) {
                m_rerender.AddRange(collection: declared);
            }
        }
        foreach (var name in m_standing) {
            if (!m_rerender.Contains(item: name)) {
                m_rerender.Add(item: name);
            }
        }

        RenderGraphScheduler.Schedule(
            frame: (frame with {
                Rerender = m_rerender,
            }),
            history: prior,
            schedule: schedule,
            set: m_set
        );

        foreach (var released in m_released) {
            schedule.Next.Forget(index: released);
        }
    }
    // A kept instance's output in a reconfigured set: what it stands for renumbered into the new set, or, when its
    // producer retires, an output standing for a retired image, which resolves to nothing.
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
