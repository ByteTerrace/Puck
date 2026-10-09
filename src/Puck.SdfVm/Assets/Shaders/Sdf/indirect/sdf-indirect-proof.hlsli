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

// Successful proofs transfer through overlapping certified balls inside the same cell and anchor bin. A procedure
// (sdfIndirectProveStep): each attempt's segment is a call, and an owner's uncertified publication samples its point,
// both answered by its owner's run.
struct SdfIndirectProveProc {
    float3 position;
    uint level;
    uint budget;
    float certifiedClearance;
    uint mask;
    uint start;
    float spacing;
    int index;
    uint cellAddress;
    uint components;
    uint key;
    uint proof;
    bool receiverOwner;
    SdfIndirectPlacement corners[8];
    uint tried;
    uint preferred;
    uint attempt;
    uint component;
    uint allowance;
    float clearance;
    uint phase;
};
static SdfIndirectProveProc sdfIndirectProveProc = (SdfIndirectProveProc)0;

static const uint SdfIndirectProveBegun = 0u;
static const uint SdfIndirectProveSegmented = 1u;
static const uint SdfIndirectProveSampled = 2u;

uint sdfIndirectProveBegin(float3 position, uint level, uint budget, float certifiedClearance) {
    sdfIndirectProveProc.position = position;
    sdfIndirectProveProc.level = level;
    sdfIndirectProveProc.budget = budget;
    sdfIndirectProveProc.certifiedClearance = certifiedClearance;
    sdfIndirectProveProc.phase = SdfIndirectProveBegun;
    return SdfIndirectProcProve;
}

// The publication of a proved mask, or the release of the receiver's transient claim: everything after the field work.
void sdfIndirectProveFinish() {
#if defined(SDF_INDIRECT_PASS) || defined(SDF_RECEIVER_PASS)
    uint proof = sdfIndirectProveProc.proof;
    float clearance = sdfIndirectProveProc.clearance;
    if (sdfIndirectProveProc.mask != 0u && clearance > 0.0 && isfinite(clearance)) {
        bool publish = true;
#ifdef SDF_INDIRECT_PASS
        uint previous;
        InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, previous);
        publish = previous == 0u;
#endif
        if (publish) {
            float3 position = sdfIndirectProveProc.position;
            sdfIndirectStore(proof, asuint(position.x));
            sdfIndirectStore(proof + 1u, asuint(position.y));
            sdfIndirectStore(proof + 2u, asuint(position.z));
            sdfIndirectStore(proof + 3u, asuint(clearance));
            sdfIndirectStore(proof + 4u, sdfIndirectProveProc.mask);
            DeviceMemoryBarrier();
            sdfIndirectStore(proof + 5u, sdfIndirectProveProc.key);
            DeviceMemoryBarrier();
            sdfIndirectStore(proof + 6u, passGroup.indirectFrame);
#ifdef SDF_RECEIVER_PASS
            sdfIndirectProveProc.receiverOwner = false;
#endif
        }
    }
#ifdef SDF_RECEIVER_PASS
    // Failed support certifies no obstruction for neighboring receivers. Only this pixel may retain its unresolved
    // result. Release its transient claim instead of publishing a transferable negative proof.
    if (sdfIndirectProveProc.receiverOwner) { sdfIndirectStore(proof + 6u, 0u); }
#endif
#endif
    sdfIndirectProofEvaluations += sdfIndirectEvaluations - sdfIndirectProveProc.start;
}

uint sdfIndirectProveStep() {
    if (sdfIndirectProveProc.phase == SdfIndirectProveBegun) {
        sdfIndirectProveProc.mask = 0u;
        sdfIndirectProveProc.start = sdfIndirectEvaluations;
        float3 position = sdfIndirectProveProc.position;
        uint level = sdfIndirectProveProc.level;
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
        sdfIndirectProveProc.spacing = spacing;
        int3 cell;
        if (level >= sdfIndirectLevelCount() || !sdfIndirectCellAt(position, spacing, cell)) { return SdfIndirectStepReturn; }
        int index = sdfIndirectProbeIndex(cell, level);
        if (!sdfIndirectCellCurrent(index)) { return SdfIndirectStepReturn; }
        sdfIndirectProveProc.index = index;
        uint cellAddress = sdfIndirectCellWordOffset(passGroup.indirectTier) + (uint)index * SdfIndirectCellWords;
        sdfIndirectProveProc.cellAddress = cellAddress;
        sdfIndirectProveProc.components = sdfIndirectLoad(cellAddress);
        if (sdfIndirectProveProc.components == 0xffffffffu) { return SdfIndirectStepReturn; }
        uint key;
        uint entry = sdfIndirectProofEntry((uint)index, cell, sdfIndirectProofSlot(position, spacing), level, key);
        sdfIndirectProveProc.key = key;
        uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + entry * SdfIndirectProofWords;
        sdfIndirectProveProc.proof = proof;
        if (!sdfIndirectRange(proof, SdfIndirectProofWords)) { return SdfIndirectStepReturn; }
        sdfIndirectHashes++;
#if defined(SDF_INDIRECT_PASS) || defined(SDF_RECEIVER_PASS)
        uint cachedMask;
        if (sdfIndirectReuseProof(proof, key, passGroup.indirectFrame, position, sdfIndirectProveProc.certifiedClearance, spacing, cachedMask)) {
            sdfIndirectProveProc.mask = cachedMask;
            return SdfIndirectStepReturn;
        }
#endif
        sdfIndirectProveProc.receiverOwner = false;
#ifdef SDF_RECEIVER_PASS
        if (!sdfIndirectReceiverPermit && passGroup.indirectReceiverProofs == 0u) {
            sdfIndirectReceiverDeferred = true;
            return SdfIndirectStepReturn;
        }
        // Claim before evaluating the segment. Pending and same-submission publications have no readable payload yet;
        // defer without inspecting their key. An older non-reusable occupant keeps the bounded uncached fallback.
        uint publication;
        InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, publication);
        sdfIndirectProveProc.receiverOwner = publication == 0u;
        if (!sdfIndirectProveProc.receiverOwner && publication >= passGroup.indirectFrame) {
            sdfIndirectReceiverDeferred = true;
            sdfIndirectReceiverPermit = false;
            return SdfIndirectStepReturn;
        }
        if (!sdfIndirectReceiverPermit && !sdfIndirectAdmitReceiver()) {
            if (sdfIndirectProveProc.receiverOwner) { sdfIndirectStore(proof + 6u, 0u); }
            return SdfIndirectStepReturn;
        }
        sdfIndirectReceiverPermit = false;
#endif
        [unroll] for (uint c = 0u; c < 8u; c++) { sdfIndirectProveProc.corners[c] = sdfIndirectReadProbe(sdfIndirectProbeIndex(cell + sdfIndirectCorner(c), level)); }
        sdfIndirectProveProc.tried = 0u;
        // Plane sign changes the preferred component only. Every accepted component still needs a field segment.
        uint plane = sdfIndirectLoad(cellAddress + 1u);
        sdfIndirectProveProc.preferred = plane == 0u ? 0u : (dot(sdfIndirectUnpackNormal(plane), position) >= asfloat(sdfIndirectLoad(cellAddress + 2u)) ? 1u : 0u);
        sdfIndirectProveProc.attempt = 0u;
    } else if (sdfIndirectProveProc.phase == SdfIndirectProveSegmented) {
        sdfIndirectProveProc.budget -= sdfIndirectProveProc.allowance - sdfIndirectSegmentProc.budget;
        if (sdfIndirectSegmentProc.clear) {
            uint components = sdfIndirectProveProc.components;
            [unroll] for (uint c = 0u; c < 8u; c++) {
                if (((components >> (c * 4u)) & 15u) == sdfIndirectProveProc.component && sdfIndirectProveProc.corners[c].classification != SdfIndirectClassInactive) { sdfIndirectProveProc.mask |= 1u << c; }
            }
            // The attempts end at the first clear component.
            sdfIndirectProveProc.attempt = 2u;
        } else {
            sdfIndirectProveProc.attempt++;
        }
    } else {
        sdfIndirectProveProc.clearance = sdfMapBallClearance(sdfIndirectReply.distance);
        sdfIndirectProveFinish();
        return SdfIndirectStepReturn;
    }
    if (sdfIndirectProveProc.attempt < 2u && sdfIndirectProveProc.budget > 0u) {
        uint candidate = 8u;
        float closest = 1.0e30;
        uint components = sdfIndirectProveProc.components;
        [unroll] for (uint c = 0u; c < 8u; c++) {
            uint component = (components >> (c * 4u)) & 15u;
            if (component == 15u || (sdfIndirectProveProc.tried & (1u << component)) != 0u || sdfIndirectProveProc.corners[c].classification == SdfIndirectClassInactive) { continue; }
            float distance = length(sdfIndirectProveProc.corners[c].position - sdfIndirectProveProc.position);
            if (sdfIndirectProveProc.attempt == 0u && component != sdfIndirectProveProc.preferred) { distance += sdfIndirectProveProc.spacing * 8.0; }
            if (distance < closest) { closest = distance; candidate = c; }
        }
        if (candidate != 8u) {
            sdfIndirectProveProc.component = (components >> (candidate * 4u)) & 15u;
            sdfIndirectProveProc.tried |= 1u << sdfIndirectProveProc.component;
            sdfIndirectProveProc.allowance = min(sdfIndirectProveProc.budget, SdfIndirectSegmentSteps);
            sdfIndirectProveProc.phase = SdfIndirectProveSegmented;
            return sdfIndirectCall(sdfIndirectSegmentBegin(sdfIndirectProveProc.position, sdfIndirectProveProc.corners[candidate].position, sdfIndirectProveProc.allowance));
        }
    }
    // Trace retains its single source-ray owner. The receiver claimed the empty slot before field work.
    // Every reader rejects this submission's stamp, including its pending marker and partially written payload.
    sdfIndirectProveProc.clearance = 0.0;
#if defined(SDF_INDIRECT_PASS) || defined(SDF_RECEIVER_PASS)
    bool owns = false;
#ifdef SDF_INDIRECT_PASS
    owns = sdfIndirectProofOwner == (uint)sdfIndirectProveProc.index;
#else
    owns = sdfIndirectProveProc.receiverOwner;
#endif
    if (sdfIndirectProveProc.mask != 0u && owns) {
        float certified = sdfIndirectProveProc.certifiedClearance;
        sdfIndirectProveProc.clearance = isfinite(certified) ? max(0.0, certified) : 0.0;
        if (sdfIndirectProveProc.clearance == 0.0 && sdfIndirectProveProc.budget > 0u) {
            sdfIndirectProveProc.budget--;
            sdfIndirectProveProc.phase = SdfIndirectProveSampled;
            return sdfIndirectAsk(sdfIndirectProveProc.position, SDF_INSTANCE_MASK_ALL);
        }
    } else {
        // Not this procedure's to publish: no clearance, so the finish only releases a receiver claim.
        sdfIndirectProveProc.clearance = 0.0;
    }
#endif
    sdfIndirectProveFinish();
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_PROVE
uint sdfIndirectProve(float3 position, uint level, inout uint budget, float certifiedClearance = 0.0) {
    sdfIndirectRun(sdfIndirectProveBegin(position, level, budget, certifiedClearance));
    budget = sdfIndirectProveProc.budget;
    return sdfIndirectProveProc.mask;
}
#endif
#endif
