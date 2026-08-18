using Puck.Maths;

namespace Puck.World.Server;

internal readonly record struct BodySensorTarget(int Index, FixedVector3 Position, FixedQ4816 DistanceSquared) {
    public bool Exists => (Index >= 0);
    public static BodySensorTarget None => new(
        Index: -1,
        Position: default,
        DistanceSquared: FixedQ4816.MaxValue
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
