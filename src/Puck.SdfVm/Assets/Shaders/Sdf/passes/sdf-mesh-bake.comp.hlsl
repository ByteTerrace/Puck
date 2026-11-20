// The mesh bake's field sampler: one invocation per sample of the dispatch's batch of lattices (the pass block's
// meshBakeLattices, each sample x fastest within its lattice, the lattices back to back), evaluating the instances its
// lattice's instance mask selects (sdf-mesh-bake-mask.comp.hlsl) at the sample and, when the lattice asks for gradients, at
// four tetrahedral taps around it, through the kernel's one interpreter call site. Every transform the walk reads comes
// from the poses the lattice's group bakes through (the lattice's fourth row, SDF_MESH_BAKE_POSES in
// field/sdf-program.hlsli), never from this frame's: a lattice whose selector names a dynamic-transform slot lies in that
// slot's frame, which those poses hold at the identity, and its gradient comes back in that frame. The host meshes the
// samples (SdfMeshPathBake). This is bake-time work: the mesh path's frame dispatches it only while a bake is pending.
//
// Each lattice names its output base and fields in glyphs.w. Value and material planes are contiguous;
// gradient triples follow the value plane, in the order the contour consumes them.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_MESH_BAKE_POSES
#define SDF_INSTANCE_MASKS
#define SDF_SAMPLED_REGIONS
#define SDF_GLYPH_ATLAS
#include "sdf-bake-world.hlsli"
#include "../isa/sdf-bake-taps.hlsli"

// The samples are packed in dispatch order (SdfMeshBakeBlock.Write). A partial final workgroup may run past the end.
void sdfBakeSample(uint sample, uint count) {
    uint lattice = 0u;
    uint high = count;
    [loop]
    while (lattice + 1u < high) {
        uint middle = (lattice + high) >> 1u;
        if (asuint(passGroup.meshBakeLattices[middle * SDF_MESH_BAKE_LATTICE_ROWS].w) <= sample) {
            lattice = middle;
        } else {
            high = middle;
        }
    }

    uint rowBase = lattice * SDF_MESH_BAKE_LATTICE_ROWS;
    float4 origin = passGroup.meshBakeLattices[rowBase];
    uint4 dims = asuint(passGroup.meshBakeLattices[rowBase + 2u]);
    uint local = sample - asuint(origin.w);
    if (local >= dims.x * dims.y * dims.z) { return; }

    float4 cell = passGroup.meshBakeLattices[rowBase + 1u];
    uint mode = dims.w & 3u;
    float3 position;
    if (mode >= 2u) {
        position = sdfMeshBakePoints[sample].xyz;
    } else {
        // A slab shares the final lattice's origin. Its packed first plane places it on that grid.
        uint plane = (dims.w & 0xFFFFu) >> 2u;
        uint3 grid = uint3(local % dims.x, (local / dims.x) % dims.y, local / (dims.x * dims.y) + plane);
        position = origin.xyz + float3(grid) * cell.xyz;
    }

    uint4 poses = asuint(passGroup.meshBakeLattices[rowBase + 3u]);
    float4 glyphs = passGroup.meshBakeLattices[rowBase + 4u];
    sdfGlyphCell = glyphs.x;
    sdfGlyphStroke = glyphs.y;
    sdfBakePoseBase = poses.x;
    sdfBakePoseFirst = poses.y;
    sdfBakePoseCount = poses.z;
    sdfBakeLivePoses = (glyphs.z != 0.0);
    // Only the world selector samples segments outside every instance.
    sdfMapOmitWorldSegments = (asuint(cell.w) != 0x80000000u);

    float4 orientation = float4(0.0, 0.0, 0.0, 1.0);
    int frame = asint(poses.w);
    if (frame >= 0) {
        uint pose = 3u * (uint)frame;
        orientation = sdfDynamicTransformRow(pose + 1u);
        position = rotatePointByQuaternion(position, orientation) + sdfDynamicTransformRow(pose).xyz;
    }

    uint maskBase = worldInstanceMaskBase(lattice);
    uint lastTap = (mode == 3u) ? SDF_BAKE_GRADIENT_TAPS : 0u;
    float spacing = cell.x;
    float3 gradient = 0.0;
    uint outputMode = asuint(glyphs.w) & 3u;
    uint outputBase = asuint(glyphs.w) >> 2u;
    uint samples = dims.x * dims.y * dims.z;
    uint word = outputBase + local;
    sdfProgramLayout = sdfLoadProgramLayout();
    [loop]
    for (uint tap = 0u; tap <= lastTap; tap++) {
        // Sample in the field's frame, but difference along the lattice's axes for the contour.
        float3 direction = sdfBakeTap(tap);
        float3 at = position;
        if (tap != 0u) { at += rotatePointByQuaternion(direction * spacing, orientation); }
        SdfHit hit = mapCore(at, maskBase, tap == 0u);
        if (tap == 0u) {
            meshBakeSamplesRW[word] = outputMode == 2u ? asuint(hit.material) : asuint(hit.distance);
            if (outputMode == 1u) { meshBakeSamplesRW[word + samples] = asuint(hit.material); }
        } else {
            gradient += direction * hit.distance;
        }
        sdfWorkSteps++;
    }

    // The four taps sum to four times their spacing times the gradient.
    if (outputMode == 3u) {
        gradient *= (0.25 / spacing);
        uint at = outputBase + samples + 3u * local;
        meshBakeSamplesRW[at] = asuint(gradient.x);
        meshBakeSamplesRW[at + 1u] = asuint(gradient.y);
        meshBakeSamplesRW[at + 2u] = asuint(gradient.z);
    }
    sdfWorkTexels = 1u;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (passGroup.meshBakeCount != 0u) { sdfBakeSample(id.x, passGroup.meshBakeCount); }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
