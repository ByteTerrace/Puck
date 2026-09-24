using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The frame a rule-fired <c>applyRigidImpulse</c> reads its heading body's facing in.</summary>
public sealed partial class WorldRuleHost {
    // Forward in this engine's body-local frame is -Z, the axis WorldBody reads its own facing along. The impulse
    // strikes a different body than the one supplying the heading, so it reads the heading body's orientation here.
    private static readonly FixedVector3 RigidImpulseLocalForward = -FixedVector3.UnitZ;
}
