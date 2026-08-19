using Puck.Maths;

namespace Puck.World.Server;

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
        Index: WorldTargetDesignation.PointIndex,
        Position: position,
        DistanceSquared: distanceSquared
    );
}
internal readonly record struct BodyProducerSensors(BodySensorTarget Candidate, BodySensorTarget CurrentTarget);
internal struct BodyProducerState {
    public int AcquiredTarget;
    public FixedQ4816 ActivityPhase;
    public FixedQ4816 ActivityRate;
    public FixedQ4816 Phase;
    public FixedQ4816 PreferredAltitude;
    public FixedQ4816 WeaveFrequency;
}
