using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The cue gesture's one honest impulse path: a rule-fired <c>applyRigidImpulse</c> resolves its struck and
/// heading bodies through the same live body-reference resolver a spatial rule channel reads
/// (<see cref="ResolveBodyRef(CompiledBodyRef, ulong)"/>), then hands the magnitude straight to the rigid solver
/// <c>body.impulse</c> already drives (<see cref="WorldBody.TryApplyRigidImpulse"/>) — never a second impulse
/// mechanism.</summary>
public sealed partial class WorldServer {
    // Forward in this engine's body-local frame is -Z (WorldBody's own private UnitZ convention, mirrored here since
    // an impulse strikes a different body than the one supplying the heading — every existing facing read stays
    // inside WorldBody itself).
    private static readonly FixedVector3 RigidImpulseLocalForward = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -FixedQ4816.One);

    // Returns true (refused) or false (applied/skipped) on the same terms FireBodyEffect/FireFieldPaint already do.
    private bool FireRigidImpulseEffect(RigidImpulseEffect effect, string ruleName, ulong tick, bool preflight) {
        var targetIndex = ResolveBodyRef(bodyRef: effect.Target, tick: tick);

        if (Body(index: targetIndex) is not { } target) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"applyRigidImpulse key resolves to no active body (index {targetIndex})");

            return true;
        }
        if (!target.IsRigid) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.RigidBodyRequired, ruleName: ruleName, effect: effect, tick: tick, detail: $"body:{targetIndex} carries no rigid kit facet — see world.rigid");

            return true;
        }

        var headingIndex = ResolveBodyRef(bodyRef: effect.Heading, tick: tick);

        if (Body(index: headingIndex) is not { } heading) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"applyRigidImpulse headingKey resolves to no active body (index {headingIndex})");

            return true;
        }

        var magnitudeFact = effect.Magnitude.Read(reader: (IRuleReader)this);

        if (magnitudeFact.IsAbsent || magnitudeFact.IsForever) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.RigidImpulseOutOfRange, ruleName: ruleName, effect: effect, tick: tick, detail: "applyRigidImpulse magnitude cell is absent");

            return true;
        }

        if (preflight) {
            return false;
        }

        var magnitude = FixedQ4816.FromRawBits(value: magnitudeFact.ToRaw(kind: CellKind.Fixed));
        var direction = heading.FixedOrientation.Rotate(vector: RigidImpulseLocalForward);

        if (!target.TryApplyRigidImpulse(impulse: (direction * magnitude), velocityCeiling: m_population.RigidVelocityCeiling)) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.RigidImpulseOutOfRange, ruleName: ruleName, effect: effect, tick: tick, detail: $"body:{targetIndex} impulse is not representable or would exceed the world's declared speed ceiling ({(double)m_population.RigidVelocityCeiling:0.###})");

            return true;
        }

        return false;
    }
}
