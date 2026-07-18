// The carve-union brick baker (carve-bake plan §3) — a tiny, standalone compute kernel: ONE thread per voxel. It reads
// a bin's settled sphere-carve list from a request buffer and writes the closed-form UNION distance
// min_i(|v - c_i| - r_i) / lambda into the persistent brick pool at the brick's base word. The values are pre-scaled by
// 1/lambda (lambda = sqrt(3) folded in at bake), which makes the trilinear interpolant the render kernels sample
// (sdfSampledRegion in sdf-vm.hlsli) 1-Lipschitz and march-safe with NO stepScale change and an unchanged zero set.
//
// DELIBERATE closed form (not a grid of map() calls): v1 brick content is exactly a hard-Subtraction sphere-union, so
// the closed form needs no program buffer, instance masks, or blend tail — the general map()-grid baker is the recorded
// follow-up that reuses the SAME ISA op when baked content outgrows carve unions.
//
// SLICED (async / off the live edit path — verdict-row 55): the engine advances a voxel cursor across produced frames,
// dispatching <= 256K voxels per frame. The push constants carry THIS dispatch's [sliceVoxelStart, sliceVoxelCount)
// window; slicing changes no value (each voxel is written exactly once, independent of slice boundaries).
//
// Request buffer (StructuredBuffer<float4>): a fixed 3-float4 header, then the carve list.
//   req[0] = (boxMin.xyz, cellSize)
//   req[1] = asfloat(dimX, dimY, dimZ, carveCount)                 [uint bits]
//   req[2] = (asfloat(destWordOffset) [uint bits], invLambda, 0, 0)
//   req[3 .. 3 + carveCount) = (center.xyz, radius) per carve
// The linear voxel index is x-fastest: destWordOffset + x + y*dimX + z*dimX*dimY (KEEP IN SYNC with sdfBrickVoxel's
// fetch ordering in sdf-vm.hlsli and the SdfBrickBake request packing in SdfWorldEngine).

[[vk::binding(0, 0)]] StructuredBuffer<float4> bakeRequest : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<float> brickPool : register(u0);

struct BrickBakePush {
    uint sliceVoxelStart; // the first global voxel index this dispatch writes
    uint sliceVoxelCount; // how many voxels this dispatch writes (<= 256K)
    uint pad0;
    uint pad1;
};
[[vk::push_constant]] BrickBakePush bakePush;

static const uint BrickBakeHeaderFloat4Count = 3u;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint local = id.x;

    if (local >= bakePush.sliceVoxelCount) {
        return;
    }

    uint voxel = (bakePush.sliceVoxelStart + local);

    float4 header0 = bakeRequest[0];
    float4 header1 = bakeRequest[1];
    float4 header2 = bakeRequest[2];

    float3 boxMin = header0.xyz;
    float cellSize = header0.w;
    uint dimX = asuint(header1.x);
    uint dimY = asuint(header1.y);
    uint dimZ = asuint(header1.z);
    uint carveCount = asuint(header1.w);
    uint destWordOffset = asuint(header2.x);
    float invLambda = header2.y;

    uint total = (dimX * dimY * dimZ);

    if (voxel >= total) {
        return;
    }

    // Decompose the linear voxel index (x-fastest) into its lattice coordinate, then the voxel CENTRE world position
    // (voxel i is centred at local = i + 0.5 — KEEP IN SYNC with sdfSampledRegion's `local - 0.5` sample convention).
    uint x = (voxel % dimX);
    uint y = ((voxel / dimX) % dimY);
    uint z = (voxel / (dimX * dimY));
    float3 p = (boxMin + ((float3(x, y, z) + 0.5) * cellSize));

    // The settled-carve UNION distance: min over the sphere carves of (|p - c| - r). An empty carve set leaves the far
    // field (a Subtraction compose of it never bites — the uncarved hull).
    float d = 1e30;

    for (uint i = 0u; (i < carveCount); i++) {
        float4 carve = bakeRequest[(BrickBakeHeaderFloat4Count + i)];

        d = min(d, (length(p - carve.xyz) - carve.w));
    }

    brickPool[(destWordOffset + voxel)] = (d * invLambda);
}
