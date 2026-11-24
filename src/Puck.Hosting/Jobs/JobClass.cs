namespace Puck.Hosting.Jobs;

/// <summary>
/// The class a <see cref="JobPool"/> fork runs at. A worker that finishes a partition takes its next from the
/// highest class with partitions left, so one class's fork preempts another's at the next partition boundary, never
/// inside a partition. Within a class a worker takes from the fork opened last, so a fork nested in a running partition
/// (a parallel wave's tick forking its population) is served before the wave's remaining partitions.
/// </summary>
public enum JobClass {
    /// <summary>The frame's own parallel work, forked and joined by the frame thread inside produce: the pose pack,
    /// and later view recording.</summary>
    Frame = 0,
    /// <summary>The simulation tick's partitions, forked and joined by the thread that steps an authority.</summary>
    Tick = 1,
}
