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

// A shared proof's anchor is its bin's centre, never the point of whichever receiver computed it first: the record
// is a pure function of the bin, so a reader's answer cannot depend on the order receivers claim, publish or collide.
float3 sdfIndirectProofAnchor(int3 slot, float spacing) {
    precise float3 scaled = (float3(slot) + 0.5) * (spacing / (float)SdfIndirectProofSlotsPerAxis);
    return scaled;
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

// The bin's published canonical record. Pending markers and this submission's own publications are never read.
bool sdfIndirectCanonicalProof(uint proof, uint key, uint frame, out uint mask, out float3 anchor, out float clearance) {
    mask = 0u;
    anchor = 0.0;
    clearance = 0.0;
    uint published = sdfIndirectLoad(proof + 6u);
    if (published == 0u || published >= frame || sdfIndirectLoad(proof + 5u) != key) { return false; }
    anchor = asfloat(uint3(sdfIndirectLoad(proof), sdfIndirectLoad(proof + 1u), sdfIndirectLoad(proof + 2u)));
    clearance = asfloat(sdfIndirectLoad(proof + 3u));
    mask = sdfIndirectLoad(proof + 4u);
    return true;
}

// A proof certifies clear segments from a point to the corner probes of one component of its cell. A private proof
// (transport, near-field) starts from the caller's own point within the caller's allowance and is never shared. A
// shared receiver proof first takes its bin's canonical proof, computed from the bin's centre within the fixed
// SdfIndirectCanonicalProofSteps allowance and published once per bin; a receiver whose certified ball reaches that
// anchor's ball inherits its mask, and every other receiver proves its own point privately. Whether a record was
// read from the table or computed again, the answer is the same, so no receiver's result depends on timing.
// A procedure (sdfIndirectProveStep): each attempt's segment is a call, and the anchor's clearance is a query, both
// answered by its owner's run.
struct SdfIndirectProveProc {
    float3 position;
    uint level;
    uint budget;
    float certifiedClearance;
    bool canonical;
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
    uint stage;
    float3 origin;
    float3 anchor;
    uint ownBudget;
    uint canonicalMask;
    uint tried;
    uint preferred;
    uint attempt;
    uint component;
    uint allowance;
    uint phase;
};
static SdfIndirectProveProc sdfIndirectProveProc = (SdfIndirectProveProc)0;

static const uint SdfIndirectProveBegun = 0u;
static const uint SdfIndirectProveSegmented = 1u;
static const uint SdfIndirectProveSampled = 2u;
static const uint SdfIndirectProveOwn = 0u;
static const uint SdfIndirectProveCanonical = 1u;

uint sdfIndirectProveBegin(float3 position, uint level, uint budget, float certifiedClearance, bool canonical = false) {
    sdfIndirectProveProc.position = position;
    sdfIndirectProveProc.level = level;
    sdfIndirectProveProc.budget = budget;
    sdfIndirectProveProc.certifiedClearance = certifiedClearance;
    sdfIndirectProveProc.canonical = canonical;
    sdfIndirectProveProc.phase = SdfIndirectProveBegun;
    return SdfIndirectProcProve;
}

// Starts the component attempts of one stage from its own origin; the plane side picks the preferred component only.
void sdfIndirectProveStart(uint stage, float3 origin, uint budget) {
    sdfIndirectProveProc.stage = stage;
    sdfIndirectProveProc.origin = origin;
    sdfIndirectProveProc.budget = budget;
    sdfIndirectProveProc.mask = 0u;
    sdfIndirectProveProc.tried = 0u;
    sdfIndirectProveProc.attempt = 0u;
    uint plane = sdfIndirectLoad(sdfIndirectProveProc.cellAddress + 1u);
    sdfIndirectProveProc.preferred = plane == 0u ? 0u
        : (dot(sdfIndirectUnpackNormal(plane), origin) >= asfloat(sdfIndirectLoad(sdfIndirectProveProc.cellAddress + 2u)) ? 1u : 0u);
}

// Everything after the field work: the release of a receiver's claim that published nothing, and the counted queries.
void sdfIndirectProveFinish() {
#ifdef SDF_RECEIVER_PASS
    if (sdfIndirectProveProc.receiverOwner) {
        sdfIndirectStore(sdfIndirectProveProc.proof + 6u, 0u);
        sdfIndirectProveProc.receiverOwner = false;
    }
#endif
    sdfIndirectProofEvaluations += sdfIndirectEvaluations - sdfIndirectProveProc.start;
}

// The canonical record is complete: publish it when this receiver claimed the bin, then either inherit its mask or
// prove the receiver's own point. A record whose anchor has no clearance or no clear component transfers nothing.
// Returns whether the receiver's answer is final.
bool sdfIndirectProveCanonicalDone(float clearance) {
    uint mask = sdfIndirectProveProc.canonicalMask;
    float3 anchor = sdfIndirectProveProc.anchor;
#ifdef SDF_RECEIVER_PASS
    if (sdfIndirectProveProc.receiverOwner) {
        uint proof = sdfIndirectProveProc.proof;
        sdfIndirectStore(proof, asuint(anchor.x));
        sdfIndirectStore(proof + 1u, asuint(anchor.y));
        sdfIndirectStore(proof + 2u, asuint(anchor.z));
        sdfIndirectStore(proof + 3u, asuint(clearance));
        sdfIndirectStore(proof + 4u, mask);
        DeviceMemoryBarrier();
        sdfIndirectStore(proof + 5u, sdfIndirectProveProc.key);
        DeviceMemoryBarrier();
        sdfIndirectStore(proof + 6u, passGroup.indirectFrame);
        sdfIndirectProveProc.receiverOwner = false;
    }
#endif
    if (mask != 0u && sdfIndirectProofReusable(sdfIndirectProveProc.position, sdfIndirectProveProc.certifiedClearance,
        anchor, clearance, sdfIndirectProveProc.spacing)) {
        sdfIndirectProveProc.mask = mask;
        return true;
    }
    sdfIndirectProveStart(SdfIndirectProveOwn, sdfIndirectProveProc.position, sdfIndirectProveProc.ownBudget);
    return false;
}

uint sdfIndirectProveStep() {
    if (sdfIndirectProveProc.phase == SdfIndirectProveBegun) {
        sdfIndirectProveProc.mask = 0u;
        sdfIndirectProveProc.receiverOwner = false;
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
        sdfIndirectProveProc.ownBudget = sdfIndirectProveProc.budget;
        uint stage = SdfIndirectProveOwn;
#ifdef SDF_RECEIVER_PASS
        if (sdfIndirectProveProc.canonical) {
            int3 slot = sdfIndirectProofSlot(position, spacing);
            uint key;
            uint entry = sdfIndirectProofEntry((uint)index, cell, slot, level, key);
            sdfIndirectProveProc.key = key;
            uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + entry * SdfIndirectProofWords;
            sdfIndirectProveProc.proof = proof;
            if (!sdfIndirectRange(proof, SdfIndirectProofWords)) { return SdfIndirectStepReturn; }
            sdfIndirectHashes++;
            sdfIndirectProveProc.anchor = sdfIndirectProofAnchor(slot, spacing);
            uint cachedMask;
            float3 cachedAnchor;
            float cachedClearance;
            bool cached = sdfIndirectCanonicalProof(proof, key, passGroup.indirectFrame, cachedMask, cachedAnchor, cachedClearance);
            if (cached && cachedMask != 0u && sdfIndirectProofReusable(position, sdfIndirectProveProc.certifiedClearance, cachedAnchor, cachedClearance, spacing)) {
                sdfIndirectProveProc.mask = cachedMask;
                return SdfIndirectStepReturn;
            }
            if (!sdfIndirectReceiverPermit && passGroup.indirectReceiverProofs == 0u) {
                sdfIndirectReceiverDeferred = true;
                return SdfIndirectStepReturn;
            }
            if (!cached) {
                // Claim before evaluating the anchor. A pending or same-submission publication has no readable payload
                // yet: defer without inspecting its key. A different bin's older record is computed again here, unpublished.
                uint publication;
                InterlockedCompareExchange(indirectCacheRW[proof + 6u], 0u, 0xffffffffu, publication);
                sdfIndirectProveProc.receiverOwner = publication == 0u;
                if (!sdfIndirectProveProc.receiverOwner && publication >= passGroup.indirectFrame) {
                    sdfIndirectReceiverDeferred = true;
                    sdfIndirectReceiverPermit = false;
                    return SdfIndirectStepReturn;
                }
            }
            if (!sdfIndirectReceiverPermit && !sdfIndirectAdmitReceiver()) {
                sdfIndirectProveFinish();
                return SdfIndirectStepReturn;
            }
            sdfIndirectReceiverPermit = false;
            stage = cached ? SdfIndirectProveOwn : SdfIndirectProveCanonical;
        }
#endif
        [unroll] for (uint c = 0u; c < 8u; c++) { sdfIndirectProveProc.corners[c] = sdfIndirectReadProbe(sdfIndirectProbeIndex(cell + sdfIndirectCorner(c), level)); }
        if (stage == SdfIndirectProveCanonical) {
            sdfIndirectProveStart(SdfIndirectProveCanonical, sdfIndirectProveProc.anchor, SdfIndirectCanonicalProofSteps);
        } else {
            sdfIndirectProveStart(SdfIndirectProveOwn, position, sdfIndirectProveProc.ownBudget);
        }
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
        // The canonical anchor's clearance, answered by its owner's run.
        if (sdfIndirectProveCanonicalDone(sdfMapBallClearance(sdfIndirectReply.distance))) {
            sdfIndirectProveFinish();
            return SdfIndirectStepReturn;
        }
    }
    [loop] for (uint stageEnd = 0u; stageEnd < 2u; stageEnd++) {
        if (sdfIndirectProveProc.attempt < 2u && sdfIndirectProveProc.budget > 0u) {
            uint candidate = 8u;
            float closest = 1.0e30;
            uint components = sdfIndirectProveProc.components;
            [unroll] for (uint c = 0u; c < 8u; c++) {
                uint component = (components >> (c * 4u)) & 15u;
                if (component == 15u || (sdfIndirectProveProc.tried & (1u << component)) != 0u || sdfIndirectProveProc.corners[c].classification == SdfIndirectClassInactive) { continue; }
                float distance = length(sdfIndirectProveProc.corners[c].position - sdfIndirectProveProc.origin);
                if (sdfIndirectProveProc.attempt == 0u && component != sdfIndirectProveProc.preferred) { distance += sdfIndirectProveProc.spacing * 8.0; }
                if (distance < closest) { closest = distance; candidate = c; }
            }
            if (candidate != 8u) {
                sdfIndirectProveProc.component = (components >> (candidate * 4u)) & 15u;
                sdfIndirectProveProc.tried |= 1u << sdfIndirectProveProc.component;
                sdfIndirectProveProc.allowance = min(sdfIndirectProveProc.budget, SdfIndirectSegmentSteps);
                sdfIndirectProveProc.phase = SdfIndirectProveSegmented;
                return sdfIndirectCall(sdfIndirectSegmentBegin(sdfIndirectProveProc.origin, sdfIndirectProveProc.corners[candidate].position, sdfIndirectProveProc.allowance));
            }
        }
        if (sdfIndirectProveProc.stage == SdfIndirectProveOwn) { break; }
        // The anchor's attempts are complete. A clear anchor needs its own certified ball before it can transfer.
        sdfIndirectProveProc.canonicalMask = sdfIndirectProveProc.mask;
        if (sdfIndirectProveProc.mask != 0u && sdfIndirectProveProc.budget > 0u) {
            sdfIndirectProveProc.budget--;
            sdfIndirectProveProc.phase = SdfIndirectProveSampled;
            return sdfIndirectAsk(sdfIndirectProveProc.anchor, SDF_INSTANCE_MASK_ALL);
        }
        if (sdfIndirectProveCanonicalDone(0.0)) { break; }
    }
    sdfIndirectProveFinish();
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_PROVE
uint sdfIndirectProve(float3 position, uint level, inout uint budget, float certifiedClearance = 0.0, bool canonical = false) {
    sdfIndirectRun(sdfIndirectProveBegin(position, level, budget, certifiedClearance, canonical));
    budget = sdfIndirectProveProc.budget;
    return sdfIndirectProveProc.mask;
}
#endif
#endif
