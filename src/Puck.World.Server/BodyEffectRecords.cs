using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

internal readonly record struct BodyEffectTargets(int ProducerTarget, int AffectingSubject) {
    public int Resolve(ActionTarget target) => target switch {
        ActionTarget.ProducerTarget => ProducerTarget,
        ActionTarget.AffectingSubject => AffectingSubject,
        _ => -1,
    };
}
internal readonly record struct BodyEffectOutput(int SourceIndex, int TargetIndex, CompiledBodyInstruction Instruction);

/// <summary>One <see cref="BodyMotionOp.Generate"/> firing staged during a body's advance — collected across the whole
/// population advance pass (the same staged-output shape <see cref="WorldDesignation"/> already uses) and enqueued
/// through the ordinary mutation pipeline afterwards by <c>WorldServer.Step</c>. It carries no source entity index:
/// the site is a world-global state row, never body-relative, and the acting principal is
/// <see cref="Protocol.WorldPrincipal.World"/> for every firing regardless of which body fired it — the effect is the
/// world's authored program acting, not the seat (see that principal's own remarks).</summary>
/// <param name="Row">The draw site's row name.</param>
public readonly record struct WorldGeneratorInvocation(string Row);
