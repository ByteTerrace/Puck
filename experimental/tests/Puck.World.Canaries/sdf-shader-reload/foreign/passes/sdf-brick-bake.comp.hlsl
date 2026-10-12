// A brick baker compiled against another instruction set: it declares the sdf.bricks pass block under the stamp
// Isa00000000 rather than reading the host's generated include, so world.shaders.reload compiles it and refuses it by
// its pass block's name, keeping the baker it has.
struct ForeignBricksPass {
    [[vk::offset(0)]] uint2 extent;
};
[[vk::binding(0, 3)]] ConstantBuffer<ForeignBricksPass> passGroupIsa00000000 : register(b0, space3);
[[vk::binding(2, 3)]] RWStructuredBuffer<float> brickPool : register(u2, space3);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (id.x < passGroupIsa00000000.extent.x) {
        brickPool[id.x] = 1.0e30;
    }
}
