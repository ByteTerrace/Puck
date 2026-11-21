using Puck.Abstractions.Counting;

namespace Puck.World.Client;

/// <summary>The render-pose work's process totals (<c>world.counters world.poses</c>): the frames that resolved render
/// poses (<see cref="WorldClient.UpdateRenderPoses"/>) and the poses they resolved. A frame between two deliveries
/// resolves only the bodies that moved, so over a window with every body at rest the poses rise once a delivery and
/// the frames rise every frame.</summary>
public static class WorldPoseWork {
    /// <summary>Gets the frames that resolved render poses.</summary>
    public static WorkKind Frames { get; } = new(name: "world.poses.frames", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the render poses those frames resolved.</summary>
    public static WorkKind Resolved { get; } = new(name: "world.poses.resolved", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the process totals; read twice to measure a window.</summary>
    public static WorkCounterSet Process { get; } = new(name: "world.poses", kinds: [Frames, Resolved]);
}
