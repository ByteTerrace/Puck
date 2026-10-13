// The mesh bake's field sampler: one invocation per sample of the dispatch's batch of lattices (the pass block's
// meshBakeLattices, each sample x fastest within its lattice, the lattices back to back), evaluating the instances its
// lattice's tile mask selects (sdf-mesh-bake-mask.comp.hlsl) at the sample and, when the lattice asks for gradients, at
// four tetrahedral taps around it, through the kernel's one interpreter call site. The host meshes the samples
// (SdfMeshPathBake). This is bake-time work: the mesh path's frame dispatches it only while a bake is pending.
//
// The sample record is SdfWorldPackage.MeshBakeSampleWords words: the field value, the material, then the unnormalized
// tetrahedral gradient (zero without gradients). KEEP IN SYNC with SdfMeshPathBake's reader and SdfFrameBlock.WriteMeshBake.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_INSTANCE_MASKS
#define SDF_SAMPLED_REGIONS
#define SDF_GLYPH_ATLAS
#include "sdf-world.hlsli"

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
        uint4 dims = asuint(passGroup.meshBakeLattices[(index * 3u) + 2u]);
        uint first = asuint(passGroup.meshBakeLattices[index * 3u].w);

        if ((sample >= first) && (sample < (first + (dims.x * dims.y * dims.z)))) {
            lattice = index;
        }
    }

    if (lattice != 0xFFFFFFFFu) {
        float4 row0 = passGroup.meshBakeLattices[lattice * 3u];
        float4 row1 = passGroup.meshBakeLattices[(lattice * 3u) + 1u];
        uint4 dims = asuint(passGroup.meshBakeLattices[(lattice * 3u) + 2u]);
        uint local = (sample - asuint(row0.w));
        uint x = (local % dims.x);
        uint y = ((local / dims.x) % dims.y);
        uint z = (local / (dims.x * dims.y));
        float3 cell = row1.xyz;
        float3 position = (row0.xyz + (float3(x, y, z) * cell));
        float spacing = (0.125 * min(cell.x, min(cell.y, cell.z)));
        uint maskBase = worldInstanceMaskBase(lattice);
        // The segments no instance owns form a group of their own (the selector SdfMeshPathBake.WorldSelector, whose
        // mask selects no instance); every other group samples its instances without them.
        sdfMapOmitWorldSegments = (asuint(row1.w) != 0x80000000u);
        // A mesh carries the detail shapes (painted features, rivets) the views pass re-resolves at a field hit, since no
        // field read resolves them for a mesh pixel.
        sdfDetailShadingActive = true;
        uint taps = ((dims.w != 0u) ? 5u : 1u);
        float value = 0.0;
        int material = 0;
        float3 gradient = float3(0.0, 0.0, 0.0);

        [loop]
        for (uint tap = 0u; (tap < taps); tap++) {
            float3 at = ((tap == 0u) ? position : (position + (SdfBakeTaps[tap - 1u] * spacing)));
            SdfHit hit = mapCore(at, maskBase, (tap == 0u));

            if (tap == 0u) {
                value = hit.distance;
                material = hit.material;
            } else {
                gradient += (SdfBakeTaps[tap - 1u] * hit.distance);
            }
            sdfWorkSteps += 1u;
        }

        uint word = (sample * 5u);

        meshBakeSamplesRW[word] = asuint(value);
        meshBakeSamplesRW[word + 1u] = asuint(material);
        meshBakeSamplesRW[word + 2u] = asuint(gradient.x);
        meshBakeSamplesRW[word + 3u] = asuint(gradient.y);
        meshBakeSamplesRW[word + 4u] = asuint(gradient.z);
        sdfWorkTexels = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
