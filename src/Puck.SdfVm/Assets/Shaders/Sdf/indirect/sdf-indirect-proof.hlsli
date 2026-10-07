#ifndef SDF_INDIRECT_PROOF_HLSLI
#define SDF_INDIRECT_PROOF_HLSLI
uint sdfIndirectHash(int3 cell, uint level) {
    uint hash = level * 0x9e3779b1u;
    hash = (hash ^ (uint)cell.x) * 0x85ebca77u;
    hash = (hash ^ (uint)cell.y) * 0xc2b2ae3du;
    hash = (hash ^ (uint)cell.z) * 0x27d4eb2fu;
    hash ^= hash >> 15u;
    return hash;
}

int3 sdfIndirectProofSlot(float3 position, float spacing) {
    int3 cell;
    if (!sdfIndirectCellAt(position, spacing, cell)) { return 0; }
    float3 scaled = position / spacing;
    int3 local = min(int3(floor(frac(scaled) * (float)SdfIndirectProofSlotsPerAxis)), (int)SdfIndirectProofSlotsPerAxis - 1);
    return int3(uint3(cell) * SdfIndirectProofSlotsPerAxis + uint3(local));
}

uint sdfIndirectProofEntry(uint cellIndex, int3 cell, int3 slot, uint level, out uint key) {
    uint slotHash = sdfIndirectHash(slot, sdfIndirectHash(cell, level));
    uint3 local = uint3(slot) - uint3(cell) * SdfIndirectProofSlotsPerAxis;
    uint coordinates = local.x | (local.y << SdfIndirectProofSlotBits) | (local.z << (2u * SdfIndirectProofSlotBits));
    key = (1u << (SdfIndirectProofKeyLevelBits + 3u * SdfIndirectProofSlotBits))
        | (coordinates << SdfIndirectProofKeyLevelBits) | level;
    return cellIndex * SdfIndirectProofsPerCell + slotHash % SdfIndirectProofsPerCell;
}

bool sdfIndirectProofReusable(float3 position, float receiverClearance, float3 anchor, float anchorClearance, float spacing) {
    int3 cell, anchorCell;
    if (!sdfIndirectCellAt(position, spacing, cell) || !sdfIndirectCellAt(anchor, spacing, anchorCell)) { return false; }
    float certified = isfinite(receiverClearance) ? max(0.0, receiverClearance) : 0.0;
    return anchorClearance > 0.0 && isfinite(anchorClearance)
        && all(cell == anchorCell)
        && all(sdfIndirectProofSlot(position, spacing) == sdfIndirectProofSlot(anchor, spacing))
        && length(position - anchor) <= anchorClearance + certified;
}

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

// Successful proofs transfer through overlapping certified balls inside the same cell and anchor bin.
uint sdfIndirectProve(float3 position, uint level, inout uint budget, float certifiedClearance = 0.0) {
    uint start = sdfIndirectEvaluations;
    float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 cell;
    if (level >= sdfIndirectLevelCount() || !sdfIndirectCellAt(position, spacing, cell)) { return 0u; }
    int index = sdfIndirectProbeIndex(cell, level);
    if (!sdfIndirectCellCurrent(index)) { return 0u; }
    uint cellAddress = sdfIndirectCellWordOffset(passGroup.indirectTier) + (uint)index * SdfIndirectCellWords;
    uint components = sdfIndirectLoad(cellAddress);
    if (components == 0xffffffffu) { return 0u; }
    uint key;
    uint entry = sdfIndirectProofEntry((uint)index, cell, sdfIndirectProofSlot(position, spacing), level, key);
    uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + entry * SdfIndirectProofWords;
    if (!sdfIndirectRange(proof, SdfIndirectProofWords)) { return 0u; }
    sdfIndirectHashes++;
#if defined(SDF_INDIRECT_PASS) || defined(SDF_VIEWS_PASS)
    uint cachedMask;
    if (sdfIndirectReuseProof(proof, key, passGroup.indirectFrame, position, certifiedClearance, spacing, cachedMask)) { return cachedMask; }
#endif
#ifdef SDF_VIEWS_PASS
    if (!sdfIndirectReceiverPermit && passGroup.indirectReceiverProofs == 0u) {
        sdfIndirectReceiverDeferred = true;
        return 0u;
    }
    // Claim before evaluating the segment. Pending and same-submission publications have no readable payload yet;
    // defer without inspecting their key. An older non-reusable occupant keeps the bounded uncached fallback.
    uint publication;
    InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, publication);
    bool receiverOwner = publication == 0u;
    if (!receiverOwner && publication >= passGroup.indirectFrame) {
        sdfIndirectReceiverDeferred = true;
        sdfIndirectReceiverPermit = false;
        return 0u;
    }
    if (!sdfIndirectReceiverPermit && !sdfIndirectAdmitReceiver()) {
        if (receiverOwner) { sdfIndirectStore(proof + 6u, 0u); }
        return 0u;
    }
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
    // Trace retains its single source-ray owner. Views claimed the empty slot before field work.
    // Every reader rejects this submission's stamp, including its pending marker and partially written payload.
    bool owns = false;
#ifdef SDF_INDIRECT_PASS
    owns = sdfIndirectProofOwner == (uint)index;
#else
    owns = receiverOwner;
#endif
    if (mask != 0u && owns) {
        float clearance = isfinite(certifiedClearance) ? max(0.0, certifiedClearance) : 0.0;
        if (clearance == 0.0 && budget > 0u) {
            budget--;
            SdfHit sample = sdfIndirectSample(position, SDF_INSTANCE_MASK_ALL);
            clearance = sdfMapBallClearance(sample.distance);
        }
        if (clearance > 0.0 && isfinite(clearance)) {
#ifdef SDF_INDIRECT_PASS
            uint previous;
            InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, previous);
            if (previous != 0u) {
                sdfIndirectProofEvaluations += sdfIndirectEvaluations - start;
                return mask;
            }
#endif
            sdfIndirectStore(proof, asuint(position.x));
            sdfIndirectStore(proof + 1u, asuint(position.y));
            sdfIndirectStore(proof + 2u, asuint(position.z));
            sdfIndirectStore(proof + 3u, asuint(clearance));
            sdfIndirectStore(proof + 4u, mask);
            DeviceMemoryBarrier();
            sdfIndirectStore(proof + 5u, key);
            DeviceMemoryBarrier();
            sdfIndirectStore(proof + 6u, passGroup.indirectFrame);
#ifdef SDF_VIEWS_PASS
            receiverOwner = false;
#endif
        }
    }
#ifdef SDF_VIEWS_PASS
    // Failed support certifies no obstruction for neighboring receivers. Only this pixel may retain its unresolved
    // result. Release its transient claim instead of publishing a transferable negative proof.
    if (receiverOwner) { sdfIndirectStore(proof + 6u, 0u); }
#endif
#endif
    sdfIndirectProofEvaluations += sdfIndirectEvaluations - start;
    return mask;
}

#endif
