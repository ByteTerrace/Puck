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
// WithRerenders names it, and a frame that releases an instance another's output stood for is scheduled again
// (RescheduleAfterRelease), so the reader binds the released producer's stand-in in the same frame. Only an output
// standing for a one-frame image costs a render every frame it is shown.
public sealed partial class RenderGraphRuntime {
    // The frame outputs are read at, retained when a reconfiguration replaces the scheduling history.
    private long m_readFrame = -1;

    // The instances whose latest output resolves to nothing, or stands for a one-frame image, reused from frame to frame.
    private readonly List<string> m_standing = [];
    // The instances the frame's release forgot, reused from frame to frame.
    private readonly List<int> m_released = [];

    // What an output's image is when it is not the instance's own: another graph instance's output (Producer at least
    // zero), at the frame read or, for a previous-frame read, the frame before it; or an image bound for one frame only
    // (Producer -1, Frame that frame). A stand-in is the runtime's own and lives until the device is lost or the runtime
    // disposed, which clear every output, so an output publishing one is the instance's own.
    private readonly record struct Standing(int Producer, long Frame, bool PreviousFrame = false) {
        public static Standing Own => new(
            Frame: -1L,
            Producer: -1
        );
        public bool IsOwn => ((Producer < 0) && (Frame < 0L));
        public bool IsOneFrame => ((Producer < 0) && (Frame >= 0L));
    }

    // The newest completed output of a producer that is no newer than a frame, as recorded, standing or not.
    private Output RecordedAt(int producer, long frame) {
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
            : Output.None);
    }
    // An output as a reader at a frame sees it: an instance's own output as it is; one standing for a producer's output,
    // that producer's output no newer than the frame (the frame before it for a previous-frame read), resolved in turn;
    // one standing for a one-frame image, itself within that frame. Nothing when what it stands for is gone. A node never
    // stands for its own image, so a chain ends at another instance's own output; the bound only guards the walk.
    private Output Resolve(in Output output, long frame) {
        var resolved = output;

        for (var depth = 0; (depth <= (2 * m_nodes.Length)); depth++) {
            var standing = resolved.StandsFor;

            if (standing.IsOwn) {
                return resolved;
            }
            if (standing.IsOneFrame) {
                return ((standing.Frame == m_latest?.Frame)
                    ? resolved
                    : Output.None);
            }

            frame -= (standing.PreviousFrame ? 1L : 0L);
            resolved = RecordedAt(
                frame: frame,
                producer: standing.Producer
            );
        }

        return Output.None;
    }
    // An instance's latest output as the display, a host or a capture sees it now.
    private Output LatestOf(int index) => Resolve(
        frame: m_readFrame,
        output: in m_current[index]
    );
    // What an instance's new output stands for: nothing when its node published an image of its own, otherwise the
    // producer of the image this frame bound to the external version its node published in its output's place. A stand-in
    // bound there is the runtime's own; an external producer's leased image, or a name the instance's inputs no longer
    // bind (a retired producer's hold), is bound for this frame only.
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
    // Whether an instance's latest output must render again to stay shown: it resolves to nothing, or stands for a
    // one-frame image, which resolves to nothing once the frame it was bound in has passed.
    private bool RendersToStayShown(int index) => (
        (m_sources[index] is null) &&
        !m_current[index].StandsFor.IsOwn &&
        (m_current[index].StandsFor.IsOneFrame || (LatestOf(index: index).Frame < 0))
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
    // than an empty image. The release does not depend on what renders, so the second schedule releases nothing more; the
    // released instances are forgotten again in its history.
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
    // A kept instance's output in a reconfigured set: what it stands for renumbered into the new set, or none when its
    // producer retires, since the retired producer's images go with it.
    private static Output Renumbered(in Output output, int[] renumbered) {
        var standing = output.StandsFor;

        if (standing.Producer < 0) {
            return output;
        }

        var producer = renumbered[standing.Producer];

        return ((producer < 0)
            ? Output.None
            : (output with {
                StandsFor = (standing with {
                    Producer = producer,
                }),
            }));
    }
}
