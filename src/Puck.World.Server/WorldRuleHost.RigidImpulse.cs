using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The frame a rule-fired <c>applyRigidImpulse</c> reads its heading body's facing in.</summary>
public sealed partial class WorldRuleHost {
    // Forward in this engine's body-local frame is -Z (WorldBody's own private UnitZ convention, mirrored here since
    // an impulse strikes a different body than the one supplying the heading — every existing facing read stays
    // inside WorldBody itself).
    private static readonly FixedVector3 RigidImpulseLocalForward = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: -FixedQ4816.One
    );
}
