using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One non-human body's phased motion/steering cadence and reusable producer image.</summary>
public readonly record struct WorldPopulationAutonomyCheckpoint(
    ulong MotionPeriodTicks,
    ulong MotionElapsedTicks,
    ulong MotionRemainingTicks,
    ulong SteeringPeriodTicks,
    ulong SteeringElapsedTicks,
    ulong SteeringRemainingTicks,
    PlayerIntent SteeringIntent,
    bool SteeringSeeded
);
