using Puck.SdfVm;

namespace Puck.Demo.Gravity;

/// <summary>
/// Draws the gravity scenario's planetoid using <see cref="GravityScenario.EmitInto"/>'s exact authoring calls,
/// run against the gravity composition's shared builder — the SAME code path <see cref="GravityScenario.BuildProgram"/>
/// uses to build the standalone program <see cref="GravityScenario.BuildEvaluator"/> wraps (the single-source-of-truth
/// thesis). Static content (the planetoid never changes at runtime this wave), so this stays at the default
/// <see cref="ISdfSceneEmitter.Revision"/> (0) — Probe and live draw identically, trivially satisfying the "probe
/// dominates" contract, exactly like <see cref="Puck.Demo.Rts.RtsTerrainEmitter"/>.
/// </summary>
public sealed class PlanetoidEmitter : ISdfSceneEmitter {
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        _ = context;

        GravityScenario.EmitInto(builder: builder);
    }
}
