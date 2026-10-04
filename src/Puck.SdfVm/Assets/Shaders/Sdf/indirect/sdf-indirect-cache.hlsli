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

int sdfIndirectProbeIndex(int3 lattice, uint level) {
    int3 brick = int3(floor(float3(lattice) / 4.0));
    int3 local = lattice - brick * 4;
    uint capacity = sdfIndirectBrickCapacity(passGroup.indirectTier);
    [loop] for (uint slot = 0u; slot < capacity; slot++) {
        int4 entry = indirectBricks[slot];
        sdfIndirectLoads++;
        if (entry.w == (int)level && all(entry.xyz == brick)) {
            return (int)(slot * SdfIndirectProbesPerBrick) + local.x + local.y * 4 + local.z * 16;
        }
    }
    return -1;
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

uint sdfIndirectHash(int3 cell, uint level) {
    uint hash = level * 0x9e3779b1u;
    hash = (hash ^ (uint)cell.x) * 0x85ebca77u;
    hash = (hash ^ (uint)cell.y) * 0xc2b2ae3du;
    hash = (hash ^ (uint)cell.z) * 0x27d4eb2fu;
    hash ^= hash >> 15u;
    return hash;
}

int3 sdfIndirectProofSlot(float3 position, float spacing) {
    float3 scaled = position / spacing;
    int3 cell = int3(floor(scaled));
    int3 local = min(int3(floor(frac(scaled) * (float)SdfIndirectProofSlotsPerAxis)), (int)SdfIndirectProofSlotsPerAxis - 1);
    return cell * (int)SdfIndirectProofSlotsPerAxis + local;
}

uint sdfIndirectProofEntry(uint cellIndex, int3 cell, int3 slot, uint level, out uint key) {
    uint slotHash = sdfIndirectHash(slot, sdfIndirectHash(cell, level));
    uint3 local = uint3(slot - cell * (int)SdfIndirectProofSlotsPerAxis);
    uint coordinates = local.x | (local.y << SdfIndirectProofSlotBits) | (local.z << (2u * SdfIndirectProofSlotBits));
    key = (1u << (SdfIndirectProofKeyLevelBits + 3u * SdfIndirectProofSlotBits))
        | (coordinates << SdfIndirectProofKeyLevelBits) | level;
    return cellIndex * SdfIndirectProofsPerCell + slotHash % SdfIndirectProofsPerCell;
}

bool sdfIndirectProofReusable(float3 position, float receiverClearance, float3 anchor, float anchorClearance, float spacing) {
    float certified = isfinite(receiverClearance) ? max(0.0, receiverClearance) : 0.0;
    return anchorClearance > 0.0 && isfinite(anchorClearance)
        && all(int3(floor(position / spacing)) == int3(floor(anchor / spacing)))
        && all(sdfIndirectProofSlot(position, spacing) == sdfIndirectProofSlot(anchor, spacing))
        && length(position - anchor) <= anchorClearance + certified;
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

bool sdfIndirectReuseProof(uint proof, uint key, uint frame, float3 position, float certifiedClearance,
    float spacing, out uint mask) {
    mask = 0u;
    uint published = sdfIndirectLoad(proof + 6u);
    if (published == 0u || published >= frame || sdfIndirectLoad(proof + 5u) != key) { return false; }
    float3 anchor = asfloat(uint3(sdfIndirectLoad(proof), sdfIndirectLoad(proof + 1u), sdfIndirectLoad(proof + 2u)));
    float clearance = asfloat(sdfIndirectLoad(proof + 3u));
    if (!sdfIndirectProofReusable(position, certifiedClearance, anchor, clearance, spacing)) { return false; }
    mask = sdfIndirectLoad(proof + 4u);
    return true;
}

#ifdef SDF_INDIRECT_PASS
float3 sdfIndirectDirection(int3 lattice, uint level, uint ray) {
    float3 direction = indirectDirections[ray].xyz;
    uint symmetry = sdfIndirectHash(lattice, level) % 48u;
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

// Successful proofs transfer through overlapping certified balls inside the same cell and anchor bin.
uint sdfIndirectProve(float3 position, uint level, inout uint budget, float certifiedClearance = 0.0) {
    uint start = sdfIndirectEvaluations;
    float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell = int3(floor(position / spacing));
    int index = sdfIndirectProbeIndex(cell, level);
    if (index < 0) { return 0u; }
    uint cellAddress = sdfIndirectCellWordOffset(passGroup.indirectTier) + (uint)index * SdfIndirectCellWords;
    uint components = sdfIndirectLoad(cellAddress);
    if (components == 0xffffffffu) { return 0u; }
    uint key;
    uint entry = sdfIndirectProofEntry((uint)index, cell, sdfIndirectProofSlot(position, spacing), level, key);
    uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + entry * SdfIndirectProofWords;
    sdfIndirectHashes++;
#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
    uint cachedMask;
    if (sdfIndirectReuseProof(proof, key, passGroup.indirectFrame, position, certifiedClearance, spacing, cachedMask)) { return cachedMask; }
#endif
#ifdef SDF_VIEWS_PASS
    if (!sdfIndirectReceiverPermit && !sdfIndirectAdmitReceiver()) { return 0u; }
    sdfIndirectReceiverPermit = false;
#endif
    SdfIndirectPlacement corners[8];
    [unroll] for (uint c = 0u; c < 8u; c++) { corners[c] = sdfIndirectReadProbe(sdfIndirectProbeIndex(cell + sdfIndirectCorner(c), level)); }
    uint tried = 0u;
    uint mask = 0u;
    // Plane sign changes the preferred component only. Every accepted component still needs a field segment.
    uint plane = sdfIndirectLoad(cellAddress + 1u);
    uint preferred = plane == 0u ? 0u : (dot(sdfIndirectUnpackNormal(plane), position) >= asfloat(sdfIndirectLoad(cellAddress + 2u)) ? 1u : 0u);
    [loop] for (uint attempt = 0u; attempt < 2u && budget > 0u; attempt++) {
        uint candidate = 8u;
        float closest = 1.0e30;
        [unroll] for (uint c = 0u; c < 8u; c++) {
            uint component = (components >> (c * 4u)) & 15u;
            if (component == 15u || (tried & (1u << component)) != 0u || corners[c].classification == SdfIndirectClassInactive) { continue; }
            float distance = length(corners[c].position - position);
            if (attempt == 0u && component != preferred) { distance += spacing * 8.0; }
            if (distance < closest) { closest = distance; candidate = c; }
        }
        if (candidate == 8u) { break; }
        uint component = (components >> (candidate * 4u)) & 15u;
        tried |= 1u << component;
        float3 blocked;
        uint allowance = min(budget, SdfIndirectSegmentSteps);
        uint remaining = allowance;
        bool clear = sdfIndirectSegment(position, corners[candidate].position, remaining, blocked);
        budget -= allowance - remaining;
        if (clear) {
            [unroll] for (uint c = 0u; c < 8u; c++) {
                if (((components >> (c * 4u)) & 15u) == component && corners[c].classification != SdfIndirectClassInactive) { mask |= 1u << c; }
            }
            break;
        }
    }
#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
    // Trace retains its single source-ray owner. Views atomically claim an empty slot; every reader rejects this
    // submission's stamp, so it cannot observe a partially written anchor from another workgroup.
    bool owns = false;
#ifdef SDF_INDIRECT_PASS
    owns = sdfIndirectProofOwner == (uint)index;
#else
    owns = passGroup.indirectReceiverProofs != 0u;
#endif
    if (mask != 0u && owns) {
        float clearance = isfinite(certifiedClearance) ? max(0.0, certifiedClearance) : 0.0;
        if (clearance == 0.0 && budget > 0u) {
            budget--;
            SdfHit sample = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);
            clearance = sdfMapBallClearance(sample.distance);
        }
        if (clearance > 0.0 && isfinite(clearance)) {
            uint previous;
            InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, previous);
            if (previous != 0u) {
                sdfIndirectProofEvaluations += sdfIndirectEvaluations - start;
                return mask;
            }
            indirectCacheRW[proof] = asuint(position.x);
            indirectCacheRW[proof + 1u] = asuint(position.y);
            indirectCacheRW[proof + 2u] = asuint(position.z);
            indirectCacheRW[proof + 3u] = asuint(clearance);
            indirectCacheRW[proof + 4u] = mask;
            DeviceMemoryBarrier();
            indirectCacheRW[proof + 5u] = key;
            DeviceMemoryBarrier();
            indirectCacheRW[proof + 6u] = passGroup.indirectFrame;
        }
    }
#endif
    sdfIndirectProofEvaluations += sdfIndirectEvaluations - start;
    return mask;
}

#ifdef SDF_INDIRECT_PASS
bool sdfIndirectEndpointSupports(float3 handoff, float3 direction, float3 endpoint) {
    return dot(endpoint - handoff, direction) > 0.0;
}
// IrradianceCacheModel.ReprojectedRay: the original ray must be inside the direction cone; its endpoint must be
// beyond the handoff. Rank those endpoints as seen from the handoff, while an exit ranks by direction alone.
int sdfIndirectContinuationRay(float3 position, float3 direction, int3 lattice, uint level, uint index, float3 origin) {
    uint state = indirectTraceStates[index];
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
