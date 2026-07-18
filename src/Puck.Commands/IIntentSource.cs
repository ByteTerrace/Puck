namespace Puck.Commands;

/// <summary>
/// Supplies per-participant input that drives a fixed-tick simulation, one intent per slot per tick. This is the
/// seam that keeps a simulation independent of WHERE input comes from: a local device, the network, an AI, or a
/// recording all implement this, so the same simulation is deterministic and replayable regardless of the source.
/// </summary>
/// <typeparam name="TIntent">The per-participant intent value the simulation steps.</typeparam>
public interface IIntentSource<TIntent> {
    /// <summary>Called once per rendered frame, before the frame's ticks are stepped. A live source samples its input
    /// here; <paramref name="firstTick"/> is the first simulation tick this frame, so a frame-sampled source can fire
    /// press/release EDGES only on that tick (held state carries across the frame's remaining ticks).</summary>
    void BeginFrame(ulong firstTick);

    /// <summary>Returns the intents for one tick, one per participant in slot order (length == <paramref name="participants"/>
    /// count). The caller records exactly what this returns, so a replay that returns the recorded values reproduces
    /// the run bit-for-bit.</summary>
    /// <param name="tick">The simulation tick being stepped.</param>
    /// <param name="participants">The current participants, in slot order (their identities; index == dynamic-transform slot).</param>
    TIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> participants);
}
