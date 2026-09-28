// A brick baker that reads this host's sdf.bricks interface, stamped with its instruction set, through the generated
// include, and writes the far field into every voxel of its slice: world.shaders.reload compiles it and installs it in
// place of the shipped baker.
#include "../../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-bricks.interface.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint sliceVoxels = passGroup.extent.x;

    if (id.x < sliceVoxels) {
        brickPool[((asuint(bakeRequest[2].x) + (pushedIndex.index * sliceVoxels)) + id.x)] = 1.0e30;
    }
}
