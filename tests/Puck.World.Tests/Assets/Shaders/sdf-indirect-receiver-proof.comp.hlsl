[[vk::binding(5, 3)]] RWStructuredBuffer<uint> receiverRecords : register(u5, space3);
[[vk::binding(60, 3)]] StructuredBuffer<float4> receiverCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> receiverResults : register(u61, space3);
struct ReceiverProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<ReceiverProbeIndex> receiverProbeIndex : register(b0, space4);
struct ReceiverParameters { uint2 indirectAllocation; uint indirectCertificateRevision; };
static ReceiverParameters passGroup;
static const uint SdfVisibilityRowI = 16u;
static const uint SdfVisibilityWords = 24u;
static uint receiverWrites = 0u;
uint sdfIndirectLevelCount() { return 3u; }
#define sdfVisibilityRecordBuffer receiverRecords
void sdfVisibilityStoreRow(uint word, uint4 bits) {
    [unroll] for (uint lane = 0u; lane < 4u; lane++) { receiverRecords[word + lane] = bits[lane]; }
    receiverWrites += 4u;
}
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-receiver-certificate.hlsli"

[numthreads(1, 1, 1)]
void CSMain() {
    uint index = receiverProbeIndex.index;
    uint4 scope = asuint(receiverCases[2u * index]);
    uint4 state = asuint(receiverCases[2u * index + 1u]);
    passGroup.indirectAllocation = uint2(9u, 7u);
    passGroup.indirectCertificateRevision = 13u;
    receiverRecords[15u] = 12345u;
    receiverRecords[23u] = 0u;
    sdfIndirectStoreReceiverCertificate(0u, 2u, state.x, float3(1.25, -0.125, 8192.0), 0.03125, state.y != 0u);
    DeviceMemoryBarrier();
    passGroup.indirectAllocation = scope.xy;
    passGroup.indirectCertificateRevision = scope.z;
    uint level, mask;
    float3 launched;
    float clearance;
    bool valid = sdfIndirectReceiverCertificate(0u, level, mask, launched, clearance);
    receiverResults[uint2(index, 0u)] = float4(valid ? 1.0 : 0.0, (float)level, (float)mask, (float)receiverWrites);
    receiverResults[uint2(index, 1u)] = float4(launched, clearance);
    receiverResults[uint2(index, 2u)] = float4((float)receiverRecords[15u], 0.0, 0.0, 0.0);
}
