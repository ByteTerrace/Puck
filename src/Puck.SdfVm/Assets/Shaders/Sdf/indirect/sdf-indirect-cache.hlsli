#ifndef SDF_INDIRECT_CACHE_HLSLI
#define SDF_INDIRECT_CACHE_HLSLI
#include "sdf-indirect-cells.hlsli"
#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
#define SDF_INDIRECT_WORDS indirectCacheRW
#else
#define SDF_INDIRECT_WORDS indirectCache
#endif

static uint sdfIndirectLoads = 0u;
static uint sdfIndirectHashes = 0u;
static uint sdfIndirectProofOwner = 0xffffffffu;
uint sdfIndirectLoad(uint word) { sdfIndirectLoads++; return SDF_INDIRECT_WORDS[word]; }
#ifdef SDF_VIEWS_PASS
static bool sdfIndirectReceiverPermit = false;
static bool sdfIndirectReceiverDeferred = false;
// The finite CAS loop never overflows the counter. Contention may defer a receiver, but cannot admit past the limit.
bool sdfIndirectAdmitReceiver() {
    uint word = sdfIndirectReceiverProofWordOffset(passGroup.indirectTier);
    uint limit = passGroup.indirectReceiverProofs;
    if (limit == 0u) { sdfIndirectReceiverDeferred = true; return false; }
    uint observed = sdfIndirectLoad(word);
    [loop] for (uint attempt = 0u; attempt < 64u && observed < limit; attempt++) {
        uint previous;
        InterlockedCompareExchange(indirectCacheRW[word], observed, observed + 1u, previous);
        if (previous == observed) { return true; }
        observed = previous;
    }
    sdfIndirectReceiverDeferred = true;
    return false;
}
#endif

int3 sdfIndirectCorner(uint corner) { return int3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u); }
uint sdfIndirectLevelCount() { return passGroup.indirectTier == SdfIndirectTierHigh ? 3u : 2u; }

#include "sdf-indirect-bricks.hlsli"

bool sdfIndirectCellCurrent(int index) {
    if (index < 0) { return false; }
    sdfIndirectLoads++;
    return (indirectBricks[(uint)index / SdfIndirectProbesPerBrick].w & (int)SdfIndirectBrickClassified) != 0;
}

SdfIndirectPlacement sdfIndirectReadProbe(int index) {
    SdfIndirectPlacement placement = (SdfIndirectPlacement)0;
    placement.classification = SdfIndirectClassInactive;
    if (index < 0) { return placement; }
    uint address = (uint)index * SdfIndirectProbeWords;
    uint state = sdfIndirectLoad(address + 3u);
    if ((state >> SdfIndirectEpochShift) == 0u) { return placement; }
#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
    if ((state >> SdfIndirectEpochShift) != passGroup.indirectEpoch) { return placement; }
#endif
    placement.position = asfloat(uint3(sdfIndirectLoad(address), sdfIndirectLoad(address + 1u), sdfIndirectLoad(address + 2u)));
    placement.classification = state & SdfIndirectClassMask;
    return placement;
}

uint4 sdfIndirectPackRay(SdfIndirectRay ray, uint mask, uint level) {
    return uint4(asuint(ray.distance), sdfIndirectPackNormal(ray.normal), ray.material,
        ray.kind | (mask << SdfIndirectProofMaskShift) | (level << SdfIndirectProofLevelShift)
        | ((ray.launchHeight & SdfIndirectLaunchHeightMask) << SdfIndirectLaunchHeightShift));
}

float sdfIndirectLaunchHeight(uint terminal, float spacing) {
    return ((float)(terminal >> SdfIndirectLaunchHeightShift) / (float)SdfIndirectLaunchHeightMask) * spacing;
}

float3 sdfIndirectLaunchPosition(float3 surface, uint packedNormal, uint terminal, float spacing) {
    return surface + sdfIndirectUnpackNormal(packedNormal) * sdfIndirectLaunchHeight(terminal, spacing);
}

// The proof consumes the same point a later reader reconstructs from the stored normal and height.
uint sdfIndirectQuantizeLaunch(float3 surface, float3 normal, float spacing, inout float3 position, inout float clearance) {
    float height = max(0.0, dot(position - surface, normal));
    uint encoded = (uint)floor(saturate(height / spacing) * (float)SdfIndirectLaunchHeightMask);
    if (encoded > 0u && sdfIndirectLaunchHeight(encoded << SdfIndirectLaunchHeightShift, spacing) > height) { encoded--; }
    uint packedNormal = sdfIndirectPackNormal(normal);
    float3 reconstructed = sdfIndirectLaunchPosition(surface, packedNormal, encoded << SdfIndirectLaunchHeightShift, spacing);
    clearance = max(0.0, clearance - length(reconstructed - position));
    position = reconstructed;
    return encoded;
}

#ifdef SDF_INDIRECT_PASS
// Both placement and partition replace a cell through this store, invalidating every owned proof bucket.
void sdfIndirectStoreCell(uint cell, uint proof, SdfIndirectCell value) {
    [unroll] for (uint word = 0u; word < SdfIndirectProofsPerCell * SdfIndirectProofWords; word++) {
        indirectCacheRW[proof + word] = 0u;
    }
    indirectCacheRW[cell] = value.components;
    indirectCacheRW[cell + 1u] = sdfIndirectPackNormal(value.normal);
    indirectCacheRW[cell + 2u] = asuint(value.offset);
}
#endif

#include "sdf-indirect-proof.hlsli"

#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
float3 sdfIndirectDirection(int3 lattice, uint level, uint ray) {
    float3 direction = indirectDirections[ray].xyz;
    uint symmetry = sdfIndirectHash(lattice, level) % 48u;
    sdfIndirectLoads++;
    sdfIndirectHashes++;
    if ((symmetry & 1u) != 0u) { direction.x = -direction.x; }
    if ((symmetry & 2u) != 0u) { direction.y = -direction.y; }
    if ((symmetry & 4u) != 0u) { direction.z = -direction.z; }
    switch (symmetry >> 3u) {
        case 1u: return direction.xzy;
        case 2u: return direction.yxz;
        case 3u: return direction.yzx;
        case 4u: return direction.zxy;
        case 5u: return direction.zyx;
        default: return direction;
    }
}
#endif

#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
bool sdfIndirectEndpointSupports(float3 handoff, float3 direction, float3 endpoint) {
    return dot(endpoint - handoff, direction) > 0.0;
}
// IrradianceCacheModel.ReprojectedRay: the original ray must be inside the direction cone; its endpoint must be
// beyond the handoff. Rank those endpoints as seen from the handoff, while an exit ranks by direction alone.
int sdfIndirectContinuationRay(float3 position, float3 direction, int3 lattice, uint level, uint index, float3 origin) {
    uint state = indirectTraceStates[index];
    sdfIndirectLoads++;
    uint rays = sdfIndirectRaysPerProbe(passGroup.indirectTier);
    int best = -1;
    float bestCosine = -2.0;
    [loop] for (uint ray = 0u; ray < rays; ray++) {
        if ((state & (1u << (ray % (rays / 64u)))) == 0u) { continue; }
        float3 storedDirection = sdfIndirectDirection(lattice, level, ray);
        float cosine = dot(storedDirection, direction);
        if (cosine < cos(0.5)) { continue; }
        uint address = sdfIndirectHitWordOffset(passGroup.indirectTier) + (index * rays + ray) * SdfIndirectHitWords;
        uint terminal = sdfIndirectLoad(address + 3u) & SdfIndirectKindMask;
        if (terminal == SdfIndirectKindUnresolved) { continue; }
        if (terminal != SdfIndirectKindExit) {
            float3 endpoint = origin + storedDirection * asfloat(sdfIndirectLoad(address));
            if (!sdfIndirectEndpointSupports(position, direction, endpoint)) { continue; }
            cosine = dot(normalize(endpoint - position), direction);
        }
        if (cosine > bestCosine) { best = (int)ray; bestCosine = cosine; }
    }
    return best;
}

// Only traced, non-dormant corners with an endpoint beyond the handoff can support a continuation.
uint sdfIndirectSupport(float3 position, float3 direction, uint level, uint mask) {
    float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell = int3(floor(position / spacing));
    uint readable = 0u;
    [unroll] for (uint c = 0u; c < 8u; c++) {
        if ((mask & (1u << c)) == 0u) { continue; }
        int3 lattice = cell + sdfIndirectCorner(c);
        int index = sdfIndirectProbeIndex(lattice, level);
        SdfIndirectPlacement probe = sdfIndirectReadProbe(index);
        if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { continue; }
        if (sdfIndirectContinuationRay(position, direction, lattice, level, (uint)index, probe.position) >= 0) { readable |= 1u << c; }
    }
    return readable;
}
#endif
#endif
