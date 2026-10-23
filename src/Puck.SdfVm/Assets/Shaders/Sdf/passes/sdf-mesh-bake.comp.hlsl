// The mesh bake's field sampler: one invocation per sample of the dispatch's batch of lattices (the pass block's
// meshBakeLattices, each sample x fastest within its lattice, the lattices back to back), evaluating the instances its
// lattice's instance mask selects (sdf-mesh-bake-mask.comp.hlsl) at the sample and, when the lattice asks for gradients, at
// four tetrahedral taps around it, through the kernel's one interpreter call site. Every transform the walk reads comes
// from the poses the lattice's group bakes through (the lattice's fourth row, SDF_MESH_BAKE_POSES in
// field/sdf-program.hlsli), never from this frame's: a lattice whose selector names a dynamic-transform slot lies in that
// slot's frame, which those poses hold at the identity, and its gradient comes back in that frame. The host meshes the
// samples (SdfMeshPathBake). This is bake-time work: the mesh path's frame dispatches it only while a bake is pending.
//
// The sample record is SdfWorldPackage.MeshBakeSampleWords words: the field value, the material, then the field's
// gradient from the tetrahedral taps in field units per world unit, whose magnitude the contour divides a value by to
// read it as a distance (SdfSampledLattice.AnswerGradients); a points lattice without gradients writes the dynamic-
// transform slot of the shape that won the sample in the first gradient word instead (a group's vertex rides it,
// SdfMesh.Members), and zeros after it. KEEP IN SYNC with SdfMeshPathBake's reader and SdfFrameBlock.WriteMeshBake.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_MESH_BAKE_POSES
#define SDF_INSTANCE_MASKS
#define SDF_SAMPLED_REGIONS
#define SDF_GLYPH_ATLAS
#include "sdf-bake-world.hlsli"

static const float3 SdfBakeTaps[4] = {
    float3(1.0, -1.0, -1.0),
    float3(-1.0, -1.0, 1.0),
    float3(-1.0, 1.0, -1.0),
    float3(1.0, 1.0, 1.0),
};

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint sample = id.x;
    uint lattice = 0xFFFFFFFFu;
    uint count = min(passGroup.meshBakeCount, 16u);

    sdfProgramLayout = sdfLoadProgramLayout();

    [loop]
    for (uint index = 0u; (index < count); index++) {
        uint4 dims = asuint(passGroup.meshBakeLattices[(index * 4u) + 2u]);
        uint first = asuint(passGroup.meshBakeLattices[index * 4u].w);

        if ((sample >= first) && (sample < (first + (dims.x * dims.y * dims.z)))) {
            lattice = index;
        }
    }

    if (lattice != 0xFFFFFFFFu) {
        float4 row0 = passGroup.meshBakeLattices[lattice * 4u];
        float4 row1 = passGroup.meshBakeLattices[(lattice * 4u) + 1u];
        uint4 dims = asuint(passGroup.meshBakeLattices[(lattice * 4u) + 2u]);
        uint4 poses = asuint(passGroup.meshBakeLattices[(lattice * 4u) + 3u]);
        uint local = (sample - asuint(row0.w));
        uint x = (local % dims.x);
        uint y = ((local / dims.x) % dims.y);
        uint z = (local / (dims.x * dims.y));
        float3 cell = row1.xyz;
        // A points lattice (mode two, or three with gradients) samples the point table's entry at the sample's place in
        // the dispatch: a surface point of a contoured mesh whose material the bake reads, or a corner of a crossed cell
        // of a final lattice whose gradient the contour reads. A grid lattice samples its grid.
        // The mode word's high half is the instance run the mask pass reads.
        uint mode = (dims.w & 0xFFFFu);
        bool points = (mode >= 2u);
        float3 position = (points ? sdfMeshBakePoints[sample].xyz : (row0.xyz + (float3(x, y, z) * cell)));
        // The gradient taps lie a sixty-fourth of the contour's finest cell out: a crossing a tap's spacing from a sharp
        // edge reads both faces' blend, and its tangent plane cuts the edge by about that much (SdfSampledLatticeLawTests
        // samples the same stencil).
        float spacing = (0.015625 * min(cell.x, min(cell.y, cell.z)));
        uint maskBase = worldInstanceMaskBase(lattice);
        int selector = asint(row1.w);

        sdfBakePoseBase = poses.x;
        sdfBakePoseFirst = poses.y;
        sdfBakePoseCount = poses.z;

        float3 slotPosition = float3(0.0, 0.0, 0.0);
        float4 slotOrientation = float4(0.0, 0.0, 0.0, 1.0);

        if (selector >= 0) {
            slotPosition = sdfDynamicTransformRow(3u * (uint)selector).xyz;
            slotOrientation = sdfDynamicTransformRow((3u * (uint)selector) + 1u);
        }
        // The segments no instance owns form a group of their own (the selector SdfMeshBakeGroup.WorldSelector, whose
        // mask selects no instance); every other group samples its instances without them.
        sdfMapOmitWorldSegments = (asuint(row1.w) != 0x80000000u);
        uint taps = ((mode == 3u) ? 5u : 1u);
        float value = 0.0;
        int material = 0;
        int frameSlot = SDF_TRANSFORM_SLOT_NONE;
        float3 gradient = float3(0.0, 0.0, 0.0);

        [loop]
        for (uint tap = 0u; (tap < taps); tap++) {
            float3 at = ((tap == 0u) ? position : (position + (SdfBakeTaps[tap - 1u] * spacing)));
            SdfHit hit = mapCore((rotatePointByQuaternion(at, slotOrientation) + slotPosition), maskBase, (tap == 0u));

            if (tap == 0u) {
                value = hit.distance;
                material = hit.material;
                frameSlot = hit.frameSlot;
            } else {
                gradient += (SdfBakeTaps[tap - 1u] * hit.distance);
            }
            sdfWorkSteps += 1u;
        }

        // The four taps sum to four times the spacing times the gradient. mapCore scales what it returns by the program's
        // step scale, a bound every instance's warps lower for the whole program; the group's own field is that answer
        // over the step scale (SdfFieldDeviceLawTests.Instances).
        float unscale = (1.0 / sdfProgramLayout.stepScale);

        value *= unscale;
        gradient *= ((0.25 / spacing) * unscale);

        uint word = (sample * 5u);

        meshBakeSamplesRW[word] = asuint(value);
        meshBakeSamplesRW[word + 1u] = asuint(material);
        meshBakeSamplesRW[word + 2u] = ((mode == 2u) ? asuint(frameSlot) : asuint(gradient.x));
        meshBakeSamplesRW[word + 3u] = asuint(gradient.y);
        meshBakeSamplesRW[word + 4u] = asuint(gradient.z);
        sdfWorkTexels = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
