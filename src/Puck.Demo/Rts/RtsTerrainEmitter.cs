using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.Rts;

/// <summary>
/// Draws the RTS arena's raised and blocking terrain features: the dais and the boulder from
/// <see cref="RtsScenario"/>. The arena's FLAT base ground is deliberately NOT drawn here — the room's own floor
/// (<c>Puck.Demo.Overworld.OverworldFrameSource.RoomEmitter</c>) already sits at <see cref="RtsScenario.GroundY"/>,
/// and a second coplanar slab would coincident-zero-set speckle against it (see the sdf-world skill's PushField/PopField
/// remarks on never drawing coplanar surfaces) for zero visual gain — <see cref="RtsScenario.TerrainPatches"/>' base
/// entry exists purely to answer <see cref="Puck.SdfVm.Queries.IWorldQuery.TryGroundHeight"/> off the arena's floor
/// everywhere the dais doesn't override it. Static content (the arena layout never changes at runtime this wave), so
/// this stays at the default <see cref="ISdfSceneEmitter.Revision"/> (0) — Probe and live draw identically, trivially
/// satisfying the "probe dominates" contract every other emitter here works to prove.
/// </summary>
public sealed class RtsTerrainEmitter : ISdfSceneEmitter {
    private static readonly Vector3 DaisColor = new(x: 0.62f, y: 0.53f, z: 0.32f);
    private static readonly Vector3 BoulderColor = new(x: 0.40f, y: 0.40f, z: 0.42f);

    private const float BoulderRound = 0.10f;
    private const float SlabHalfHeight = 0.15f;

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var daisMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: DaisColor));
        var boulderMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: BoulderColor));

        EmitSlab(builder: builder, material: daisMaterial, minX: RtsScenario.DaisMinX, minZ: RtsScenario.DaisMinZ, maxX: RtsScenario.DaisMaxX, maxZ: RtsScenario.DaisMaxZ, topY: RtsScenario.DaisTopY, round: 0.03f);

        var boulderCenterX = (0.5f * (RtsScenario.BoulderMinX + RtsScenario.BoulderMaxX));
        var boulderCenterZ = (0.5f * (RtsScenario.BoulderMinZ + RtsScenario.BoulderMaxZ));
        var boulderHalfX = (0.5f * (RtsScenario.BoulderMaxX - RtsScenario.BoulderMinX));
        var boulderHalfZ = (0.5f * (RtsScenario.BoulderMaxZ - RtsScenario.BoulderMinZ));
        var boulderHalfY = 0.55f;

        _ = builder.ResetPoint()
            .Translate(offset: new Vector3(x: boulderCenterX, y: ((RtsScenario.GroundY + boulderHalfY) - BoulderRound), z: boulderCenterZ))
            .Box(halfExtents: new Vector3(x: (boulderHalfX - BoulderRound), y: (boulderHalfY - BoulderRound), z: (boulderHalfZ - BoulderRound)), material: boulderMaterial, round: BoulderRound);
    }

    private static void EmitSlab(SdfProgramBuilder builder, int material, float minX, float minZ, float maxX, float maxZ, float topY, float round) {
        var centerX = (0.5f * (minX + maxX));
        var centerZ = (0.5f * (minZ + maxZ));
        var halfX = (0.5f * (maxX - minX));
        var halfZ = (0.5f * (maxZ - minZ));

        _ = builder.ResetPoint()
            .Translate(offset: new Vector3(x: centerX, y: (topY - SlabHalfHeight), z: centerZ))
            .Box(halfExtents: new Vector3(x: (halfX - round), y: (SlabHalfHeight - round), z: (halfZ - round)), material: material, round: round);
    }
}
