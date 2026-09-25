// A one-off image shader: the smallest pipeline, one compute pass writing one image, with no placed surface, mesh, or
// importer behind it.
#include "image.interface.hlsli"

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    output[id.xy] = float4((float(id.x) / float(passGroup.extent.x)), (float(id.y) / float(passGroup.extent.y)), 0.5, 1.0);
}
