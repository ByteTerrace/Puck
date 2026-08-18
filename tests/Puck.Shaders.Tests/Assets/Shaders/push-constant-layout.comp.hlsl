// The push-constant packing-rule fixture: one field of every shape the rule has to place — scalars, 2-, 3-, and
// 4-vectors, at offsets that exercise the 16-byte no-straddle bump both ways (a 2-vector that fits after one
// scalar; a 3-vector that fits after one scalar but not after two; a 4-vector that always starts a row). The
// test reads the offsets DXC actually assigned out of the compiled SPIR-V and asserts the manifest's computed
// layout matches them, and pins the DXIL offsets `dxc -Fc` prints for the same source.
struct LayoutPushData {
    float a;
    float2 b;
    float c;
    float3 d;
    uint e;
    float f;
    float2 g;
    float4 h;
    int i;
    float3 j;
    uint2 k;
    uint l;
    uint m;
};
[[vk::push_constant]] ConstantBuffer<LayoutPushData> pc;

[[vk::binding(0, 0)]] RWStructuredBuffer<float> sink : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    sink[id.x] = (pc.a + pc.b.y + pc.c + pc.d.z + pc.e + pc.f + pc.g.x + pc.h.w + pc.i + pc.j.y + pc.k.y + pc.l + pc.m);
}
