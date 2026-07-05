namespace Puck.Demo.Overworld;

/// <summary>
/// Supplies the per-player input that drives the simulation, one intent per slot per fixed tick. This is the seam that
/// keeps the simulation independent of WHERE input comes from: a local gamepad, the network, an AI, or a recording all
/// implement this, so the same simulation is deterministic and replayable regardless of the source.
/// </summary>
public interface IPlayerIntentSource {
    /// <summary>Called once per rendered frame, before the frame's ticks are stepped. A live source samples its input
    /// here; <paramref name="firstTick"/> is the first simulation tick this frame, so a frame-sampled source can fire
    /// press/release EDGES only on that tick (held state carries across the frame's remaining ticks).</summary>
    void BeginFrame(ulong firstTick);

    /// <summary>Returns the intents for one tick, one per player in slot order (length == <paramref name="players"/>
    /// count). The node records exactly what this returns, so a replay that returns the recorded values reproduces the
    /// run bit-for-bit.</summary>
    /// <param name="tick">The simulation tick being stepped.</param>
    /// <param name="players">The current players, in slot order (their identities; index == dynamic-transform slot).</param>
    PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players);
}
