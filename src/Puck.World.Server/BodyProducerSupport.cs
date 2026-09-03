using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Latest deterministic workload of authored non-human update cadences.</summary>
public readonly record struct WorldAutonomyStatistics(int MotionUpdates, int MotionDeferred, int SteeringUpdates);

internal readonly record struct BodySensorTarget(int Index, FixedVector3 Position, FixedQ4816 DistanceSquared) {
    public bool Exists => ((Index >= 0) || (Index == WorldTargetDesignation.PointIndex));
    public static BodySensorTarget None => new(
        Index: -1,
        Position: default,
        DistanceSquared: FixedQ4816.MaxValue
    );

    // A designated world-space point ridden by the same sensor-target shape a body target uses: FaceSensorTarget
    // and ProduceAttendIntent read only Position/DistanceSquared, so a point steers identically. The index sentinel
    // keeps AcquiredTarget's body-index reads (>= 0 guarded) treating it as no acquired body.
    public static BodySensorTarget Point(FixedVector3 position, FixedQ4816 distanceSquared) => new(
        DistanceSquared: distanceSquared,
        Index: WorldTargetDesignation.PointIndex,
        Position: position
    );
}
internal readonly record struct BodyProducerSensors(BodySensorTarget Candidate, BodySensorTarget CurrentTarget, FixedVector3 FlockDesired = default);
internal struct BodyProducerState {
    public int AcquiredTarget;
    public FixedQ4816 ActivityPhase;
    public FixedQ4816 ActivityRate;
    public FixedQ4816 Phase;
    public FixedQ4816 PreferredAltitude;
    public FixedQ4816 WeaveFrequency;
    // Q32 raw — a curve-follow target's travelled arc length, wrapped/clamped to the compiled curve's own
    // TotalLengthRaw every advance. Held at the compiled solve's own scale (rather than FixedQ4816's Q16) so a
    // sub-Q16 authored rate still accumulates across ticks instead of rounding to a standstill; read straight into
    // CompiledCurvatureSpline.EvaluateRaw, never narrowed.
    public long CurveArcRaw;
    // The (producer name, compiled curve index) CurveArcRaw was last advanced under. Selecting a producer starts
    // it: WorldPopulation.StageProducer resets CurveArcRaw to zero whenever either differs from this tick's
    // resolved producer, which covers a plain producer switch, a same-name kit retune onto a different curve row,
    // and switching away and back (the prior selection is never matched again, since re-selecting it is itself a
    // transition). ActiveProducerCurveIndex is -1 while the active producer is not a curve-follow source.
    public string? ActiveProducerName;
    public int ActiveProducerCurveIndex;
    // The navigation domain the active producer resolves through, or -1. Kept beside the curve index so a live
    // same-name producer retune invalidates its cached route before the next sensor read.
    public int ActiveProducerNavigationDomainIndex;
    public bool FlockSeeded;
    public int FlockGeneration;
    // Cached, UNCLAMPED neighbor contribution (magnitude <= 3). Goal/heading/frame are blended afresh each step.
    public FixedVector3 FlockDesired;
    public ulong FlockRemainingTicks;
    public ulong FlockSampleOrdinal;
    // Last actually perceived target, not a live read of the target body's current pose.
    public WorldFlockObservation? FlockTarget;
    // Derived binding, restored from the checkpoint's kit/producer names. Not independent simulation state.
    public CompiledBodyProducer? FlockBinding;
}
internal struct BodyAutonomyState {
    public ulong MotionPeriodTicks;
    public ulong MotionElapsedTicks;
    public ulong MotionRemainingTicks;
    public ulong SteeringPeriodTicks;
    public ulong SteeringElapsedTicks;
    public ulong SteeringRemainingTicks;
    public PlayerIntent SteeringIntent;
    public bool SteeringSeeded;

    public void Clear() => this = default;
}
