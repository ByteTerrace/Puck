using System.Numerics;
using Puck.Demo.Overworld;
using Puck.SdfVm;

namespace Puck.Demo.Rts;

/// <summary>
/// Draws every spawned RTS unit as a small standing token, ground-snapped to its simulation-held
/// <see cref="OverworldWorld.RtsUnit.Y"/> (never re-queried here — the query binds to the SIM, per
/// <see cref="RtsScenario"/>'s remarks; this emitter only reads what <c>AdvanceRtsUnits</c> already decided). A
/// selected unit's token recolors gold, the ONLY visual feedback <c>rts.select</c> gives.
/// <para>
/// THE PROBE CONTRACT (mirrors <c>Puck.Demo.Garden.GardenRenderer</c>): <c>probeWorstCase</c> emits ALL
/// <see cref="OverworldWorld.MaxRtsUnits"/> slots at a synthetic, non-overlapping layout — the true ceiling no live
/// rebuild (fewer units) can ever exceed.
/// </para>
/// </summary>
public sealed class RtsUnitInstanceEmitter : ISdfSceneEmitter {
    private static readonly Vector3 UnitColor = new(x: 0.24f, y: 0.36f, z: 0.78f);
    private static readonly Vector3 SelectedColor = new(x: 0.95f, y: 0.82f, z: 0.18f);

    private const float HalfHeight = 0.42f;
    private const float HalfWidth = 0.22f;
    private const float Round = 0.06f;

    private readonly OverworldWorld m_world;

    /// <summary>Wraps the sim world whose <see cref="OverworldWorld.RtsUnits"/> pool this draws.</summary>
    /// <param name="world">The live sim world.</param>
    public RtsUnitInstanceEmitter(OverworldWorld world) {
        ArgumentNullException.ThrowIfNull(argument: world);

        m_world = world;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var unitMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: UnitColor));
        var selectedMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: SelectedColor));
        var units = m_world.RtsUnits;

        for (var slot = 0; (slot < OverworldWorld.MaxRtsUnits); slot++) {
            var unit = ((slot < units.Count) ? units[slot] : default);
            var occupied = (context.Probe || unit.Active);

            if (!occupied) {
                continue;
            }

            var position = (context.Probe
                ? new Vector3(x: ((slot * 0.4f) - 2.2f), y: RtsScenario.GroundY, z: -5.5f)
                : new Vector3(x: (float)unit.X, y: (float)unit.Y, z: (float)unit.Z));
            var selected = (!context.Probe && unit.Selected);
            var material = (selected ? selectedMaterial : unitMaterial);

            _ = builder.ResetPoint()
                .Translate(offset: (position + new Vector3(x: 0f, y: HalfHeight, z: 0f)))
                .Box(halfExtents: new Vector3(x: (HalfWidth - Round), y: (HalfHeight - Round), z: (HalfWidth - Round)), material: material, round: Round);
        }
    }

    /// <inheritdoc/>
    public int Revision => (int)m_world.CurrentTick;
}
