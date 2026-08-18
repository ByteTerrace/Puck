namespace Puck.World.Client;

/// <summary>The narrow read of the composition root's authoritative clock a frame source needs. Declared here so a
/// Client-side type can hold the clock without naming the root's concrete simulation type.</summary>
public interface IWorldSimulationClock {
    /// <summary>Gets the exact completed simulation time in engine ticks.</summary>
    ulong ElapsedTicks { get; }
    /// <summary>Gets the world's completed-step ordinal.</summary>
    ulong Tick { get; }
}
