using System.Diagnostics;
using System.Numerics;
using Puck.Physics.Motion;

namespace Puck.World.Client;

/// <summary>One body as an authority's latest complete delivery left it: what a client knows of a body it does not
/// simulate. Every member is one delivery's, so the two positions are one step apart.</summary>
/// <param name="Active">Whether the delivery holds the body.</param>
/// <param name="Tick">The authority tick the delivery reports.</param>
/// <param name="StepSeconds">The length, in seconds, of the step the delivery reports.</param>
/// <param name="Generation">The slot's activation generation.</param>
/// <param name="PoseEpoch">The counter that moves when the body is activated, teleported or snapped.</param>
/// <param name="Kit">The body's kit row in the delivered definition.</param>
/// <param name="PreviousPosition">The position the body held one step earlier, or <paramref name="Position"/> when the
/// delivery snapped it.</param>
/// <param name="Position">The body's position.</param>
/// <param name="Orientation">The body's attitude.</param>
/// <param name="Heading">The body's heading, in radians about world up.</param>
/// <param name="Facts">The body's fact mask.</param>
public readonly record struct WorldDeliveredBody(bool Active, ulong Tick, float StepSeconds, int Generation, int PoseEpoch, byte Kit, Vector3 PreviousPosition, Vector3 Position, Quaternion Orientation, float Heading, BodyFacts Facts) {
    /// <summary>Gets the body's velocity over the delivered step, in metres per second: its displacement between the
    /// two delivered positions, zero when the step has no length or the delivery snapped the body.</summary>
    public Vector3 Velocity => ((StepSeconds > 0f) ? ((Position - PreviousPosition) / StepSeconds) : Vector3.Zero);
}
/// <summary>What a session mirror's deliveries have cost: each kind's count and the time its delivering thread spent
/// applying it, in <see cref="Stopwatch.GetTimestamp"/> units.</summary>
/// <param name="Snapshots">The tick images applied.</param>
/// <param name="SnapshotTicks">The time spent applying them.</param>
/// <param name="Definitions">The whole definitions applied.</param>
/// <param name="DefinitionTicks">The time spent applying them, their subscribers included.</param>
/// <param name="States">The value-only definition deliveries applied.</param>
/// <param name="StateTicks">The time spent applying them, their subscribers included.</param>
public readonly record struct WorldDeliveryWork(long Snapshots, long SnapshotTicks, long Definitions, long DefinitionTicks, long States, long StateTicks) {
    /// <summary>Gets the time spent applying every kind, in milliseconds.</summary>
    public double Milliseconds => (((SnapshotTicks + DefinitionTicks + StateTicks) * 1000.0) / Stopwatch.Frequency);

    /// <summary>Returns the work done between an earlier reading and a later one.</summary>
    /// <param name="later">The later reading.</param>
    /// <param name="earlier">The earlier reading.</param>
    /// <returns>The difference, member by member.</returns>
    public static WorldDeliveryWork operator -(WorldDeliveryWork later, WorldDeliveryWork earlier) => new(
        DefinitionTicks: (later.DefinitionTicks - earlier.DefinitionTicks),
        Definitions: (later.Definitions - earlier.Definitions),
        SnapshotTicks: (later.SnapshotTicks - earlier.SnapshotTicks),
        Snapshots: (later.Snapshots - earlier.Snapshots),
        StateTicks: (later.StateTicks - earlier.StateTicks),
        States: (later.States - earlier.States)
    );
}
