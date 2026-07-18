using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The serial traffic one side of a cable produced over a linked run: how many internal-clock bytes it sent as
/// master, how many transfers completed on it (master or slave — the count of serial IRQs it raised), and an FNV-1a
/// fingerprint of the exact byte stream it shifted in at each completion. A non-idle fingerprint (not the all-<c>0xFF</c>
/// of an unplugged port) is the proof that real bytes crossed the cable, and its equality across two runs is part of the
/// replay-identical proof.</summary>
/// <param name="MasterSends">The number of internal-clock (master) byte transfers this side started.</param>
/// <param name="Completions">The number of transfers that completed on this side (each raised a serial interrupt).</param>
/// <param name="TrafficHash">FNV-1a over every completed transfer's final SB byte, in order — a compact stream fingerprint.</param>
internal readonly record struct LinkSideTraffic(int MasterSends, int Completions, ulong TrafficHash);

/// <summary>The outcome of one linked replay: each side's traffic and its final whole-machine snapshot.</summary>
/// <param name="First">The first machine's serial-traffic summary.</param>
/// <param name="Second">The second machine's serial-traffic summary.</param>
/// <param name="FirstState">The first machine's final snapshot.</param>
/// <param name="SecondState">The second machine's final snapshot.</param>
internal readonly record struct LinkReplayResult(
    LinkSideTraffic First,
    LinkSideTraffic Second,
    MachineSnapshot FirstState,
    MachineSnapshot SecondState
);

/// <summary>
/// The shared deterministic driver for a linked pair of machines under per-machine input scripts: the seam both the
/// interactive explorer (<see cref="LinkExplore"/>) and the cross-generation gate (<see cref="LinkGameReplayStage"/>)
/// drive. Each frame it applies both scripts' held-button state (the multicast-into-two-streams shape) and advances the
/// pair one shared frame budget through a <see cref="SerialLinkSession"/>, while non-serialized observation hooks tally
/// each side's serial traffic. Because the scripts are pure functions of frame index and the session's interleave is a
/// pure function of the two machines' states, the whole run is replay-identical.
/// </summary>
internal static class LinkReplay {
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325ul;
    private const ulong FnvPrime = 0x100000001B3ul;

    /// <summary>Drives a linked pair for a fixed number of frames under two scripts, tallying serial traffic and (via the
    /// optional per-frame callback) letting a caller observe or dump each frame. The machines are advanced only through
    /// the returned session's shared budget; the caller still owns and disposes the machines.</summary>
    /// <param name="first">The first machine (the session's tie-break winner).</param>
    /// <param name="firstScript">The first machine's input script.</param>
    /// <param name="second">The second machine.</param>
    /// <param name="secondScript">The second machine's input script.</param>
    /// <param name="frames">The number of shared frames to run.</param>
    /// <param name="onFrame">An optional per-frame callback invoked after each frame's advance, with the frame index.</param>
    /// <returns>Each side's traffic summary and final snapshot.</returns>
    public static LinkReplayResult Run(
        MachineInstance first,
        LinkInputScript firstScript,
        MachineInstance second,
        LinkInputScript secondScript,
        int frames,
        Action<int>? onFrame = null
    ) {
        var firstJoypad = first.GetRequiredService<IJoypad>();
        var secondJoypad = second.GetRequiredService<IJoypad>();
        var firstPort = first.GetRequiredService<SerialComponent>();
        var secondPort = second.GetRequiredService<SerialComponent>();

        var firstMasterSends = 0;
        var firstCompletions = 0;
        var firstHash = FnvOffsetBasis;
        var secondMasterSends = 0;
        var secondCompletions = 0;
        var secondHash = FnvOffsetBasis;

        firstPort.ByteTransmitted = _ => ++firstMasterSends;
        firstPort.TransferCompleted = value => {
            ++firstCompletions;
            firstHash = ((firstHash ^ value) * FnvPrime);
        };
        secondPort.ByteTransmitted = _ => ++secondMasterSends;
        secondPort.TransferCompleted = value => {
            ++secondCompletions;
            secondHash = ((secondHash ^ value) * FnvPrime);
        };

        try {
            using var session = new SerialLinkSession(first: first, second: second);

            for (var frame = 0; (frame < frames); ++frame) {
                firstJoypad.SetButtons(pressed: firstScript.ButtonsAt(frame: frame));
                secondJoypad.SetButtons(pressed: secondScript.ButtonsAt(frame: frame));
                session.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
                onFrame?.Invoke(obj: frame);
            }
        } finally {
            firstPort.ByteTransmitted = null;
            firstPort.TransferCompleted = null;
            secondPort.ByteTransmitted = null;
            secondPort.TransferCompleted = null;
        }

        return new LinkReplayResult(
            First: new LinkSideTraffic(MasterSends: firstMasterSends, Completions: firstCompletions, TrafficHash: firstHash),
            Second: new LinkSideTraffic(MasterSends: secondMasterSends, Completions: secondCompletions, TrafficHash: secondHash),
            FirstState: first.Machine.Snapshot(),
            SecondState: second.Machine.Snapshot()
        );
    }
}
