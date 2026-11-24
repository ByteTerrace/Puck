// A vector's component at a runtime index, chosen by comparison. A dynamic index (`v[i]`) on a local or
// constant-buffer vector makes DXC copy the vector into a local array, which NVIDIA places in per-thread local memory: a
// store of every component and a load each time it runs (kernels.md: index local arrays by constants only). An index past
// the last component selects that component.
#ifndef FIELD_SDF_COMPONENT_HLSLI
#define FIELD_SDF_COMPONENT_HLSLI

float sdfComponent(float3 v, uint i) {
    return (i == 0u) ? v.x : ((i == 1u) ? v.y : v.z);
}
void sdfSetComponent(inout float3 v, uint i, float value) {
    if (i == 0u) { v.x = value; }
    else if (i == 1u) { v.y = value; }
    else { v.z = value; }
}
float sdfComponent(float4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}
uint sdfComponent(uint4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}
int sdfComponent(int4 v, uint i) {
    return ((i == 0u) ? v.x : ((i == 1u) ? v.y : ((i == 2u) ? v.z : v.w)));
}
// A matrix's row at a runtime index, by the same comparisons.
float3 sdfComponent(float3x3 m, uint i) {
    return (i == 0u) ? m[0] : ((i == 1u) ? m[1] : m[2]);
}
void sdfSetComponent(inout float3x3 m, uint i, float3 row) {
    if (i == 0u) { m[0] = row; }
    else if (i == 1u) { m[1] = row; }
    else { m[2] = row; }
}

#endif
