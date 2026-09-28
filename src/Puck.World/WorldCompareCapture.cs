using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>Requests comparison images through the ordinary instance capture target, then consumes them on the next
/// presentation pump before that pump replaces the captured frame's viewport metadata.</summary>
/// <param name="comparison">The session's per-seat held frames.</param>
/// <param name="viewports">The frame presenter's seat metadata, absent in a headless host.</param>
public sealed class WorldCompareCapture(WorldFrameComparison comparison, WorldSeatViewports? viewports = null) : IDisposable {
    private sealed record Pending(int Slot, bool Hold, FrameCaptureRequest Request, CommandSettlement Settlement);

    private Func<ICaptureRequestTarget>? m_target;
    private Pending? m_pending;
    private Func<ulong?>? m_completedFrames;

    private readonly WorldSeatView[] m_prepared = new WorldSeatView[PlayerRoster.MaxSlots];
    private readonly WorldSeatView[] m_published = new WorldSeatView[PlayerRoster.MaxSlots];

    private ulong? m_preparedFrame;
    private bool m_hasPrepared;

    /// <summary>Gets or sets the late command-result fan-out used by the terminal and editor toast.</summary>
    public Action<CommandResult>? Report { get; set; }

    /// <summary>Attaches the live root's existing capture target; it excludes the comparison wrapper itself.</summary>
    /// <param name="target">The current live root's capture target.</param>
    /// <param name="completedFrames">The live root's completed-frame ordinal; null for an external root with no node.</param>
    public void Attach(Func<ICaptureRequestTarget> target, Func<ulong?>? completedFrames = null) {
        m_target = target;
        m_completedFrames = completedFrames;
        m_hasPrepared = false;
        Array.Clear(array: m_published);
    }
    /// <summary>Remembers the viewports prepared for the upcoming render. A paused graph keeps its earlier published
    /// viewports until its frame counter advances, even when the next dress changes the layout.</summary>
    public void RecordPreparedFrame() {
        if (viewports is null) { return; }
        for (var slot = 0; (slot < m_prepared.Length); slot++) { m_prepared[slot] = viewports.Seat(slot: slot); }
        m_preparedFrame = m_completedFrames?.Invoke();
        m_hasPrepared = true;
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
    /// <summary>Consumes a completed PNG before the frame presenter publishes the next frame's seat viewports.</summary>
    public void Poll() {
        if (m_hasPrepared && ((m_preparedFrame is null) || (m_completedFrames?.Invoke() != m_preparedFrame))) {
            Array.Copy(sourceArray: m_prepared, destinationArray: m_published, length: m_prepared.Length);
        }
        if (m_pending is not { Request.Completion.IsCompleted: true } pending) { return; }
        m_pending = null;
        var result = pending.Request.Completion.GetAwaiter().GetResult();
        CommandResult verdict;

        try {
            if (result.Error is { } failure) { throw new InvalidOperationException(message: failure.Message, innerException: failure); }
            var frame = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: result.Path));
            var view = m_published[pending.Slot];

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
