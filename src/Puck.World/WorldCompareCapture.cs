using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Requests comparison images through the ordinary instance capture target and crops each by the seat viewports
/// prepared for the frame that rendered it, which the capture names (<see cref="FrameCaptureResult.Frame"/>), however many
/// frames later it completes.</summary>
/// <param name="comparison">The session's per-seat held frames.</param>
/// <param name="viewports">The frame presenter's seat metadata, absent in a headless host.</param>
public sealed class WorldCompareCapture(WorldFrameComparison comparison, WorldSeatViewports? viewports = null) : IDisposable {
    private sealed record Pending(int Slot, bool Hold, FrameCaptureRequest Request, CommandSettlement Settlement);

    /// <summary>The frames whose prepared viewports stay retained: more than a captured frame's readback can trail its
    /// render by.</summary>
    public const int RetainedFrames = 8;

    private Func<ICaptureRequestTarget>? m_target;
    private Pending? m_pending;
    private Func<ulong?>? m_completedFrames;

    // The seat viewports prepared for each of the latest frames, by the ordinal the live root's render of that frame
    // leaves its frame counter at; an ordinal of zero marks an empty entry.
    private readonly WorldSeatView[][] m_frames = Enumerable.Range(count: RetainedFrames, start: 0)
        .Select(selector: static _ => new WorldSeatView[PlayerRoster.MaxSlots]).ToArray();
    private readonly ulong[] m_ordinals = new ulong[RetainedFrames];

    /// <summary>Gets or sets the late command-result fan-out used by the terminal and editor toast.</summary>
    public Action<CommandResult>? Report { get; set; }

    /// <summary>Attaches the live root's existing capture target; it excludes the comparison wrapper itself.</summary>
    /// <param name="target">The current live root's capture target.</param>
    /// <param name="completedFrames">The live root's completed-frame counter, which a capture of it names its frame by;
    /// returns null while the live root renders through no node.</param>
    public void Attach(Func<ICaptureRequestTarget> target, Func<ulong?> completedFrames) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(completedFrames);
        m_target = target;
        m_completedFrames = completedFrames;
        Array.Clear(array: m_ordinals);
    }
    /// <summary>Remembers the viewports prepared for the upcoming render under the ordinal that render will leave the live
    /// root's frame counter at. A frame that does not render (a paused graph) is prepared again under the same ordinal, so
    /// the frame that last rendered keeps its own viewports.</summary>
    public void RecordPreparedFrame() {
        if ((viewports is null) || (m_completedFrames?.Invoke() is not { } completed)) { return; }
        var ordinal = (completed + 1);
        var index = ((int)(ordinal % RetainedFrames));
        var frame = m_frames[index];

        for (var slot = 0; (slot < frame.Length); slot++) { frame[slot] = viewports.Seat(slot: slot); }
        m_ordinals[index] = ordinal;
    }
    /// <summary>Gets whether the named seat has an outstanding hold or measurement.</summary>
    /// <param name="slot">The zero-based seat.</param>
    public bool IsPending(int slot) => (m_pending?.Slot == slot);
    /// <summary>Starts a hold or changed-pixel measurement and returns its eventual command verdict.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <param name="hold">Whether to replace the hold, rather than measure against it.</param>
    /// <returns>A refusal or a settlement completed after the ordinary capture.</returns>
    public CommandResult Request(int slot, bool hold) {
        if (m_target is null) { return Refuse(reason: "requires a rendered world"); }
        if (m_pending is not null) { return Refuse(reason: "a comparison capture is already pending"); }
        if (viewports?.Seat(slot: slot).Present != true) { return Refuse(reason: "the seat has no presented view"); }
        if (!hold && (comparison.Seat(slot: slot) is null)) { return Refuse(reason: "hold a frame before comparing"); }
        var request = new FrameCaptureRequest(path: Path.Combine(path1: Path.GetTempPath(), path2: $"puck-compare-{Guid.NewGuid():N}.png"));
        var settlement = new CommandSettlement();

        try {
            m_target().RequestCapture(request: request);
        } catch (Exception error) when ((error is InvalidOperationException or ArgumentException)) {
            return Refuse(reason: error.Message);
        }
        m_pending = new Pending(Hold: hold, Request: request, Settlement: settlement, Slot: slot);
        return CommandResult.Settling(settlement: settlement, late: result => Report?.Invoke(obj: result));
    }
    /// <summary>Consumes a completed PNG, cropped by the viewports prepared for the frame that rendered it.</summary>
    public void Poll() {
        if (m_pending is not { Request.Completion.IsCompleted: true } pending) { return; }
        m_pending = null;
        var result = pending.Request.Completion.GetAwaiter().GetResult();
        CommandResult verdict;

        try {
            if (result.Error is { } failure) { throw new InvalidOperationException(message: failure.Message, innerException: failure); }
            var view = ViewOf(frame: result.Frame, slot: pending.Slot);
            var frame = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: result.Path));

            if (pending.Hold) {
                comparison.Hold(slot: pending.Slot, frame: frame, view: view, tick: (result.Tick ?? 0UL));
            } else {
                _ = comparison.Measure(slot: pending.Slot, frame: frame, view: view);
            }
            verdict = Echo(slot: pending.Slot);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)) {
            verdict = Refuse(reason: error.Message);
        } finally {
            DeleteCapture(path: result.Path);
        }
        pending.Settlement.Settle(result: verdict);
    }
    /// <summary>Reports one seat's current mode and the latest measured changed-pixel count.</summary>
    /// <param name="slot">The zero-based seat.</param>
    /// <returns>The session's comparison facts as a command echo.</returns>
    public CommandResult Echo(int slot) {
        var held = comparison.Seat(slot: slot);

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"[world.compare: seat={PlayerRoster.DisplayNumber(slot: slot)} held={(held is not null)} mode={(held?.Mode ?? WorldCompareMode.Off).ToString().ToLowerInvariant()} wipe={(held?.Wipe ?? 0.5f):0.####} tick={(held?.Tick ?? 0UL)} changed-pixels={(held?.Difference?.ChangedPixels ?? 0L)} max-delta={(held?.Difference?.MaxDelta ?? 0)}]")
        );
    }

    // The viewport prepared for a seat in the frame a capture names.
    private WorldSeatView ViewOf(ulong? frame, int slot) {
        if (frame is not { } ordinal) { throw new InvalidOperationException(message: "the capture names no rendered frame"); }
        var index = ((int)(ordinal % RetainedFrames));

        return ((m_ordinals[index] == ordinal)
            ? m_frames[index][slot]
            : throw new InvalidOperationException(message: $"the captured frame {ordinal} is older than the {RetainedFrames} frames whose viewports are retained"));
    }
    private static CommandResult Refuse(string reason) => CommandResult.Error(output: $"[world.compare: {reason}]");
    private static void DeleteCapture(string path) {
        try { File.Delete(path: path); } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[world.compare: cannot remove temporary capture '{path}': {error.Message}]");
        }
    }

    /// <summary>Refuses any unfinished capture so its originating command session cannot wait on a retired renderer.</summary>
    public void Dispose() {
        if (m_pending is { } pending) {
            _ = pending.Request.TryFail(error: new ObjectDisposedException(objectName: nameof(WorldCompareCapture)));
            pending.Settlement.Settle(result: Refuse(reason: "the comparison renderer was closed"));
            DeleteCapture(path: pending.Request.Path);
            m_pending = null;
        }
        m_target = null;
    }
}
