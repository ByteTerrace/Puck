using System.Globalization;
using Puck.Hosting;

namespace Puck.SdfVm;

// The content hold: a residency whose world's content stands answers a capture with the content it holds, fitted to this
// frame's views where a view moved, and composes its world again at least ContentHoldHertz times a second. A world seen
// through a window (a painting, a lab's portal) costs a capture of its whole composition only while something in it moves.
//
// Invariant: a capture is answered from held content only when
//   - the two most recent full captures composed one content (CaptureContentSignature, read after each full capture's
//     pack): the late record that the world stood over the last interval, with its presented tick, its clocks and its
//     environment moving as they do;
//   - nothing a capture reads that can start a change has moved since the latest full capture: the frame source's own
//     early records (SdfCompositionFrameSource.TryRefit: the emitters' revision components, no build in flight, the held
//     program under the current slot layout) and its dresser's (ISdfRefitDresser.TryRefit: the world's delivered state
//     and bodies, its replay cursors and cues, the presentation's levers);
//   - the frame neither locks presentation to a tick, converges its lighting, nor holds for a capture, the views lever is
//     on and the host is paced to a display;
//   - the latest full capture is younger than one period of ContentHoldHertz (SdfUpdateRateTiers.Holds).
// What enforces it: one door. TryHoldContent is the only place a capture is answered from held content, and both capture
// paths (Capture, BeginCapturePacking) pass through it after their film gate. Where it stops: a time-driven change that was
// at rest over the last interval and starts on its own without moving an early record (a driver leaving a still phase)
// shows at the next full capture, at most one period late; a dresser that reads a new input and does not add it to its
// early records is held for the same period. Presentation only: nothing here reaches simulation state.
public sealed partial class SdfWorldResidency {
    /// <summary>Gets the least times a second a residency whose world's content stands composes it again: the slowest of
    /// the update tiers (<see cref="SdfUpdateRateTiers.Tiers"/>). Between those captures every capture is answered from
    /// the held content (<see cref="ISdfRefitDresser"/>).</summary>
    public static int ContentHoldHertz => SdfUpdateRateTiers.Tiers[^1];

    // The content signature of the latest full capture (SdfWorldTables.CaptureContentSignature), whether one was read, and
    // whether it equals the one before it: the late record that arms the hold.
    private ulong m_contentSignature;
    private bool m_contentSigned;
    private bool m_contentStill;
    // Whether the frame the tables are about to pack came from a full capture, whose content signature its pack reads.
    private bool m_packSignsContent;
    // The presentation seconds since the latest full capture, through the frames answered from held content.
    private double m_heldSeconds;
    // The views the held frame was dressed or fitted with, by value: a dresser writes its view list again in place.
    private readonly List<SdfViewSnapshot> m_heldViews = [];
    // The captures this residency ran in full, answered from its held frame, and answered with its held content fitted
    // to new views.
    private long m_fullCaptures;
    private long m_contentHolds;
    private long m_contentRefits;

    // Answers this frame's capture from the held content when the invariant above holds: stands on the held frame when no
    // view moved, else writes a frame of the held content with the views and settings the dresser fitted, which the
    // tables pack as any captured frame. Called after a capture path's film gate, never under a converging capture.
    private bool TryHoldContent(in FrameContext context) {
        if (!m_contentStill || HoldsFrame || m_programPending || (m_pendingFrame is not null) || (m_pipelineWaiting is not null) ||
            m_parked || (m_tables is null) || (SourceWaiting is not null) || (m_frameSource is not SdfCompositionFrameSource source) ||
            (m_frame is not { TickLocked: false, ConvergesLighting: false } held) || !ReferenceEquals(objA: held, objB: m_packedFrame) ||
            ((held.UpdateRates & SdfUpdateRates.Views) == 0)) {
            return false;
        }

        var since = (m_heldSeconds + context.FrameDeltaSeconds);

        if (!SdfUpdateRateTiers.Holds(elapsedSeconds: since, frameSeconds: context.FrameDeltaSeconds, hertz: ContentHoldHertz, rate: PresentationRate.Of(context: in context)) ||
            !source.TryRefit(held: held, height: m_height, settings: out var settings, views: out var views, width: m_width)) {
            return false;
        }

        m_heldSeconds = since;
        // The next full capture advances its world by the frames answered from held content, as by frames nothing read.
        m_skippedSeconds += context.FrameDeltaSeconds;
        // Views equal by value and in order to those the held frame was dressed or fitted with.
        if ((settings == held.Settings) && views.SequenceEqual(second: m_heldViews)) {
            m_contentHolds++;
            SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.ContentHeld);
            return true;
        }

        var writer = CaptureWriter();

        _ = writer.CopyFrom(content: (held.Content with { ProgramChanged = false, Views = views }), source: held);
        m_contentRefits++;
        SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.ContentRefit);
        PublishCaptured(frame: writer.Write(settings: in settings), full: false);
        return true;
    }
    // Notes a published frame for the hold: its views by value, and for a full capture, that its pack reads the content
    // signature and that the hold's period starts again.
    private void NoteHeldContent(SdfFrame frame, bool full) {
        m_heldViews.Clear();
        for (var index = 0; (index < frame.Views.Count); index++) { m_heldViews.Add(item: frame.Views[index]); }
        m_packSignsContent = (full && (m_frameSource is SdfCompositionFrameSource { Refits: true }));
        if (full) {
            m_heldSeconds = 0d;
            m_fullCaptures++;
        }
    }
    // Reads the content signature of a full capture the tables just packed, on the packing thread: the hold is armed while
    // two full captures in a row compose one content.
    private void SignContent(SdfWorldTables tables, SdfFrame frame) {
        if (!m_packSignsContent) { return; }
        m_packSignsContent = false;

        var signature = tables.CaptureContentSignature(frame: frame);

        m_contentStill = (m_contentSigned && (signature == m_contentSignature));
        m_contentSignature = signature;
        m_contentSigned = true;
        if (m_contentStill) { SdfCaptureWork.ProcessWork.Count(kind: SdfCaptureWork.ContentStill); }
    }
    // Disarms the hold until two full captures compose one content again: new tables, a park, a device loss.
    private void ForgetContent() {
        m_contentStill = false;
        m_contentSigned = false;
        m_packSignsContent = false;
    }
    /// <summary>Describes the residency's captures for <c>world.residency</c>: the full captures it ran, those it answered
    /// from its held frame and with its held content fitted to new views, and whether its content stands.</summary>
    /// <returns>One line.</returns>
    public string DescribeCaptures() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"captures full={m_fullCaptures} held={m_contentHolds} refit={m_contentRefits} content-still={(m_contentStill ? "yes" : "no")} held-for={m_heldSeconds:0.000}s");
}
