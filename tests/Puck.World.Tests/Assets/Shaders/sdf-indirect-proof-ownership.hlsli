// Synthetic one-component cell in an analytic clear field. The production segment and proof routines own every
// query, lease and publication; this fixture supplies only their field, cell directory and bounded storage.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-indirect-layout.hlsli"
[[vk::binding(5, 3)]] RWStructuredBuffer<uint> indirectCacheRW : register(u5, space3);
[[vk::binding(126, 3)]] StructuredBuffer<float4> proofCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> proofResults : register(u127, space3);
struct ProofProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<ProofProbeIndex> proofIndex : register(b0, space4);
struct ProofParameters { uint indirectTier; uint indirectFrame; uint indirectReceiverProofs; };
static ProofParameters passGroup;
static uint fixtureMode = 0u;
static uint fixturePhase = 0u;
static uint sdfIndirectEvaluations = 0u;
static uint sdfIndirectSteps = 0u;
static uint sdfIndirectProofEvaluations = 0u;
static uint sdfIndirectHashes = 0u;
static uint sdfIndirectProofOwner = 0xffffffffu;
static bool sdfIndirectReceiverPermit = false;
static bool sdfIndirectReceiverDeferred = false;
static const uint SDF_INSTANCE_MASK_ALL = 0xffffffffu;
struct SdfHit { float distance; int material; };
struct SdfIndirectPlacement { float3 position; uint classification; };
uint sdfIndirectLevelCount() { return passGroup.indirectTier == SdfIndirectTierHigh ? 3u : 2u; }
bool sdfIndirectRange(uint word, uint count) {
    uint length, stride;
    indirectCacheRW.GetDimensions(length, stride);
    return word <= length && count <= length - word;
}
uint sdfIndirectLoad(uint word) {
    if (!sdfIndirectRange(word, 1u)) { return 0xffffffffu; }
    return indirectCacheRW[word];
}
void sdfIndirectStore(uint word, uint value) {
    if (sdfIndirectRange(word, 1u)) { indirectCacheRW[word] = value; }
}
bool sdfIndirectCellAt(float3 position, float spacing, out int3 cell) {
    cell = 0;
    if (!all(isfinite(position)) || !isfinite(spacing) || spacing <= 0.0) { return false; }
    float3 scaled = floor(position / spacing);
    if (!all(isfinite(scaled)) || any(scaled < -2147483648.0) || any(scaled >= 2147483648.0)) { return false; }
    cell = int3(scaled);
    return true;
}
// The fixture's field: every sample counts in word 101 and reads its scripted distance; every gradient is +y.
#define SDF_INDIRECT_FIELD_HLSLI
// The production proof and its segments run as procedures over the fixture's field, through one run.
#define SDF_INDIRECT_PROC_PROVE
#define SDF_INDIRECT_PROC_SEGMENT
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-run.hlsli"
void sdfIndirectServe(uint kind, float3 position, uint mask, out SdfHit hit, out float3 normal) {
    hit = (SdfHit)0;
    normal = float3(0.0, 1.0, 0.0);
    if (kind == SdfIndirectQueryGradient) { return; }
    sdfIndirectEvaluations++;
    uint ignored;
    InterlockedAdd(indirectCacheRW[101u], 1u, ignored);
    hit.distance = fixturePhase == 0u && fixtureMode == 7u ? 0.0
        : (fixturePhase == 0u && fixtureMode == 9u && sdfIndirectEvaluations == 2u ? asfloat(0x7fc00000u) : 10.0);
    hit.material = 0;
}
float sdfMapBallClearance(float distance) { return distance; }
bool sdfIndirectMasked(float travelled, float reach, uint mask) { return false; }
float sdfIndirectAdvance(float clearance, float travelled, float reach, float farDistance, uint mask) {
    return max(0.0, min(clearance, farDistance - travelled));
}
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-march.hlsli"
int3 sdfIndirectCorner(uint corner) { return int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u); }
int sdfIndirectProbeIndex(int3 cell, uint level) { return cell.x | (cell.y << 1) | (cell.z << 2); }
bool sdfIndirectCellCurrent(int index) { return fixturePhase != 0u || fixtureMode != 11u; }
SdfIndirectPlacement sdfIndirectReadProbe(int index) {
    SdfIndirectPlacement result;
    result.position = (float3)sdfIndirectCorner((uint)index) * 1.5;
    result.classification = fixturePhase == 0u && fixtureMode == 13u ? SdfIndirectClassInactive : SdfIndirectClassActive;
    return result;
}
float3 sdfIndirectUnpackNormal(uint packed) { return float3(0.0, 1.0, 0.0); }
bool sdfIndirectAdmitReceiver() {
    if (passGroup.indirectReceiverProofs == 0u || (fixturePhase == 0u && fixtureMode == 8u)) {
        sdfIndirectReceiverDeferred = true;
        return false;
    }
    uint ignored;
    InterlockedAdd(indirectCacheRW[100u], 1u, ignored);
    return true;
}
// The fixture maps one cell and its eight proof buckets into the same small RW allocation.
uint fixtureCellOffset(uint tier) { return 0u; }
uint fixtureProofOffset(uint tier) { return 16u; }
#define sdfIndirectCellWordOffset fixtureCellOffset
#define sdfIndirectProofWordOffset fixtureProofOffset
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-proof.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-procedures.hlsli"
groupshared uint proofMasks[64];
groupshared uint proofDeferred[64];

[numthreads(64, 1, 1)]
void CSMain(uint lane : SV_GroupIndex) {
    uint index = proofIndex.index;
    fixtureMode = asuint(proofCases[index].x);
    passGroup.indirectTier = SdfIndirectTierMedium;
    passGroup.indirectFrame = 9u;
    passGroup.indirectReceiverProofs = fixtureMode == 15u ? 0u : 64u;
    float3 position = 0.25;
    uint key;
    uint entry = sdfIndirectProofEntry(0u, int3(0, 0, 0), int3(1, 1, 1), 0u, key);
    uint proof = 16u + entry * SdfIndirectProofWords;
    if (lane == 0u) {
        [loop] for (uint word = 0u; word < 128u; word++) { indirectCacheRW[word] = 0u; }
#ifdef SDF_RECEIVER_PASS
        if (fixtureMode >= 1u && fixtureMode <= 6u) {
            indirectCacheRW[proof] = asuint(fixtureMode == 3u ? 0.09375 : (fixtureMode == 4u ? 0.35 : 0.25));
            indirectCacheRW[proof + 1u] = asuint(fixtureMode == 3u ? 0.46875 : 0.25);
            indirectCacheRW[proof + 2u] = asuint(fixtureMode == 3u ? 0.09375 : 0.25);
            indirectCacheRW[proof + 3u] = asuint(fixtureMode == 4u ? 0.001 : (fixtureMode == 5u ? 0.0 : 1.0));
            indirectCacheRW[proof + 4u] = 165u;
            // Slots (1,1,1) and (0,2,0) collide in bucket1 but have literal full keys2340 and2112.
            indirectCacheRW[proof + 5u] = fixtureMode <= 3u ? 2112u : key;
            indirectCacheRW[proof + 6u] = fixtureMode == 1u ? 0xffffffffu : (fixtureMode == 2u ? 9u : 8u);
            if (fixtureMode == 1u || fixtureMode == 2u) {
                indirectCacheRW[proof] = 0x7fc00000u;
                indirectCacheRW[proof + 3u] = 0x7fc00000u;
            }
        }
        if (fixtureMode == 12u) { indirectCacheRW[0u] = 0xffffffffu; }
#endif
    }
    DeviceMemoryBarrierWithGroupSync();
    [loop] for (uint phase = 0u; phase < 2u; phase++) {
        fixturePhase = phase;
        sdfIndirectEvaluations = 0u;
        sdfIndirectReceiverDeferred = false;
        sdfIndirectReceiverPermit = fixtureMode == 10u;
#ifdef SDF_INDIRECT_PASS
        sdfIndirectProofOwner = lane == 0u && fixtureMode == 0u ? 0u : 0xffffffffu;
#endif
        uint budget = phase == 0u && fixtureMode == 14u ? 0u : 32u;
        float clearance = fixtureMode == 9u && phase == 0u ? 0.0 : (fixtureMode == 4u ? 0.001 : 1.0);
        proofMasks[lane] = sdfIndirectProve(position, 0u, budget, clearance);
        proofDeferred[lane] = sdfIndirectReceiverDeferred ? 1u : 0u;
        AllMemoryBarrierWithGroupSync();
        if (lane == 0u) {
            uint masks = 0u, deferred = 0u, resolved = 0u;
            [unroll] for (uint member = 0u; member < 64u; member++) {
                masks += proofMasks[member];
                resolved += proofMasks[member] != 0u ? 1u : 0u;
                deferred += proofDeferred[member];
            }
            uint publication = indirectCacheRW[proof + 6u];
            proofResults[uint2(index, phase * 2u)] = float4((float)indirectCacheRW[101u], (float)resolved, (float)deferred,
                publication == 0xffffffffu ? -1.0 : (float)publication);
            proofResults[uint2(index, phase * 2u + 1u)] = float4((float)indirectCacheRW[100u], (float)masks,
                (float)indirectCacheRW[proof + 5u], (float)entry);
            indirectCacheRW[100u] = 0u;
            indirectCacheRW[101u] = 0u;
            indirectCacheRW[0u] = 0u;
            // A stopped writer is never read. The normal transport reset releases its pending slot.
            if (fixtureMode == 1u) { indirectCacheRW[proof + 6u] = 0u; }
        }
        DeviceMemoryBarrierWithGroupSync();
        passGroup.indirectFrame = 10u;
        passGroup.indirectReceiverProofs = 64u;
    }
}
