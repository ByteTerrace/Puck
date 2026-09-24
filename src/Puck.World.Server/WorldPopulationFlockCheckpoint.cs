using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Cached local perception, timing residue, and observer-local attention stream.</summary>
/// <param name="Seeded">Whether the neighbor contribution has been sampled for this producer.</param>
/// <param name="Generation">The occupant generation that owns the attention stream.</param>
/// <param name="Desired">The unclamped, weighted neighbor contribution; goal and heading are not cached.</param>
/// <param name="RemainingTicks">Engine ticks until the next perception update.</param>
/// <param name="SampleOrdinal">Observer-local rotating sample position.</param>
/// <param name="Target">Last bounded sensed-target observation; never a live target-pose reference.</param>
public readonly record struct WorldPopulationFlockCheckpoint(bool Seeded, int Generation, FixedVector3 Desired,
    ulong RemainingTicks, ulong SampleOrdinal, WorldFlockObservation? Target = null);
