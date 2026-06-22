namespace Puck.Commands;

/// <summary>
/// The host seam that produces one fixed-step tick's <see cref="CommandSnapshot"/>. The live router builds it
/// from captured input; a replay source returns a recorded one. The host loop is identical in record and
/// replay mode — only which implementation is installed changes — so record↔replay is a one-line swap.
/// </summary>
public interface ISnapshotSource {
    /// <summary>Produces the snapshot for <paramref name="tick"/>.</summary>
    /// <param name="tick">The fixed-step tick to produce input for.</param>
    /// <param name="windowEndTick">
    /// The engine-tick time at which this tick's window closes. A live source consumes captured input whose
    /// <see cref="InputSignal.CaptureTick"/> precedes it (later input waits for a future tick); a replay source
    /// ignores it and returns the recorded snapshot verbatim.
    /// </param>
    CommandSnapshot SnapshotForTick(ulong tick, ulong windowEndTick);
}
