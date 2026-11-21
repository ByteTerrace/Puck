namespace Puck.Hosting.Jobs;

/// <summary>
/// The class a <see cref="JobPool"/> fork runs at. A worker that finishes a partition takes its next from the
/// highest class with partitions left, so one class's fork preempts another's at the next partition boundary, never
/// inside a partition. Each class holds one fork across the workers at a time; a second fork of a class whose fork is
/// in flight runs inline on its calling thread.
/// </summary>
public enum JobClass {
    /// <summary>The frame's own parallel work, forked and joined by the frame thread inside produce: the pose pack,
    /// and later view recording.</summary>
    Frame = 0,
    /// <summary>The simulation tick's partitions, forked and joined by the thread that steps an authority.</summary>
    Tick = 1,
}
