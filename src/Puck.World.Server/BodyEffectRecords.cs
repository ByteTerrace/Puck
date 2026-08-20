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
/// <summary>One <see cref="BodyMotionOp.Judge"/> firing staged during a body's advance — collected across the whole
/// population advance pass (the same staged-output shape <see cref="WorldGeneratorInvocation"/> uses) and drained by
/// <c>WorldServer.Step</c> immediately after the body step, where each is graded and folded into the server's own
/// last-grade table. Unlike a generator firing, this carries its own source entity index (the grade is per body) —
/// the grading tick itself is stamped at drain time from the step's own <c>ElapsedTicks</c> (the engine-tick domain
/// <c>MusicClock</c>/<c>RhythmJudge</c> operate in), never carried here: every invocation drained together in one
/// <c>Step</c> call shares that same instant.</summary>
/// <param name="EntityIndex">The firing body's population index.</param>
/// <param name="JudgeRef">The declared judge row name to grade against.</param>
public readonly record struct WorldJudgeInvocation(int EntityIndex, string JudgeRef);
