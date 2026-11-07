// A four-component vector's component at a runtime index, chosen by comparison. A dynamic index (`v[i]`) on a local or
// constant-buffer vector makes DXC copy the vector into a local array, which NVIDIA places in per-thread local memory: a
// store of every component and a load each time it runs (kernels.md: index local arrays by constants only). An index past
// three reads the last component.
#ifndef FRAME_SDF_COMPONENT_HLSLI
#define FRAME_SDF_COMPONENT_HLSLI

float sdfComponent(float4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}
uint sdfComponent(uint4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}
int sdfComponent(int4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}

#endif
