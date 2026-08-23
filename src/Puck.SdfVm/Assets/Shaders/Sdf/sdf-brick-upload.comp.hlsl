// The brick uploader — copies host-written voxel distances from a staging buffer into the persistent brick pool at a
// brick's base word, ONE thread per voxel. It is the CPU-baked sibling of sdf-brick-bake.comp (same binding shape:
// t0 source, u0 pool) for content a host produces itself — a field lattice's height columns — rather than a
// sphere-carve union. The values are already in the pool's stored scale (distance/lambda); nothing is rescaled.
//
// Push constants carry the window: pool[destWordOffset + i] = source[i] for i in [0, count).

[[vk::binding(0, 0)]] StructuredBuffer<float> uploadSource : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<float> brickPool : register(u0);

struct BrickUploadPush {
    uint destWordOffset; // the brick's base word in the pool
    uint count;          // voxels to copy
    uint pad0;
    uint pad1;
};
[[vk::push_constant]] BrickUploadPush uploadPush;

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint i = id.x;

    if (i >= uploadPush.count) {
        return;
    }

    brickPool[uploadPush.destWordOffset + i] = uploadSource[i];
}
