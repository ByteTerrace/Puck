#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
// Placement and partitions read the field the views draw, its baked SampledRegion bricks included.
#define SDF_SAMPLED_REGIONS
// A probe's placement and a corner pair's segments are procedures one run serves, so the kernel inlines the interpreter once.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_PLACE
#define SDF_INDIRECT_PROC_PAIR
#define SDF_INDIRECT_PROC_SEGMENT
#include "../indirect/sdf-indirect-cache.hlsli"
#include "../indirect/sdf-indirect-procedures.hlsli"

// A partition's corners and its pairs' outcomes, shared by the cell's workgroup.
groupshared SdfIndirectPlacement sdfClassifyCorners[8];
groupshared uint sdfClassifyClear[SdfIndirectCellPairs];
groupshared float3 sdfClassifyForward[SdfIndirectCellPairs];
groupshared float3 sdfClassifyReverse[SdfIndirectCellPairs];

// Placement runs a workgroup per brick and a lane per probe. A partition runs a workgroup per cell and a lane per corner
// pair, so a cell's segments march side by side rather than one after another on one lane; the cell's first lane joins
// the pairs in the sequential partition's order (sdfIndirectPartitionOfPairs). Each admission unit is a probe's placement
// or a cell's partition, and only the chunk's units run.
[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return; }
    bool placing = passGroup.indirectPhase == 0u;
    uint count = placing ? passGroup.indirectPlaceCount : passGroup.indirectClassifyCount;
    // A partition's workgroup is one unit of the chunk, counted from the first item's unit indirectUnitFirst.
    uint flat = passGroup.indirectUnitFirst + group.x;
    uint item = passGroup.indirectItemFirst + (placing ? group.x : flat / SdfIndirectProbesPerBrick);
    uint unit = placing ? lane : flat % SdfIndirectProbesPerBrick;
    bool admitted = placing ? sdfIndirectChunkUnit(group.x, lane, SdfIndirectProbesPerBrick) : (group.x < passGroup.indirectUnitCount);
    // Every test before the partition's barriers is uniform across its workgroup.
    if (item >= count || item >= sdfIndirectClassifyBudget(passGroup.indirectTier)
        || passGroup.indirectPlaceCount > sdfIndirectClassifyBudget(passGroup.indirectTier)
        || !sdfIndirectRange(0u, sdfIndirectWordCount(passGroup.indirectTier)) || !admitted) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint row = item + (placing ? 0u : passGroup.indirectPlaceCount);
    uint length, stride;
    indirectUpdates.GetDimensions(length, stride);
    if (row >= length) { return; }
    uint slot = indirectUpdates[row].x;
    indirectBricks.GetDimensions(length, stride);
    if (slot >= sdfIndirectBrickCapacity(passGroup.indirectTier) || slot >= length) { return; }
    int4 brick = indirectBricks[slot];
    uint level = (uint)brick.w & SdfIndirectBrickLevelMask;
    if (brick.w < 0 || level >= sdfIndirectLevelCount()
        || any(brick.xyz < -536870912) || any(brick.xyz > 536870911)) { return; }
    float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
    int3 lattice = brick.xyz * 4 + int3(unit & 3u, (unit >> 2u) & 3u, unit >> 4u);
    uint index = slot * SdfIndirectProbesPerBrick + unit;
    uint cell = sdfIndirectCellWordOffset(passGroup.indirectTier) + index * SdfIndirectCellWords;
    uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + index * SdfIndirectProofsPerCell * SdfIndirectProofWords;
    if (!placing) {
        if (any(lattice == 2147483647)) { return; }
        if (lane < 8u) { sdfClassifyCorners[lane] = sdfIndirectReadProbe(sdfIndirectProbeIndex(lattice + sdfIndirectCorner(lane), level)); }
        GroupMemoryBarrierWithGroupSync();
    }
    uint procedure = 0u;
    if (placing) {
        procedure = sdfIndirectPlaceBegin(float3(lattice) * spacing, spacing);
    } else if (lane < SdfIndirectCellPairs) {
        uint first, second;
        sdfIndirectCellPair(lane, first, second);
        if (sdfClassifyCorners[first].classification != SdfIndirectClassInactive && sdfClassifyCorners[second].classification != SdfIndirectClassInactive) {
            procedure = sdfIndirectPairBegin(sdfClassifyCorners[first].position, sdfClassifyCorners[second].position);
        }
    }
    if (procedure != 0u) { sdfIndirectRun(procedure); }
    if (placing) {
        SdfIndirectPlacement placement = sdfIndirectPlaceProc.result;
        uint address = index * SdfIndirectProbeWords;
        sdfIndirectStore(address, asuint(placement.position.x));
        sdfIndirectStore(address + 1u, asuint(placement.position.y));
        sdfIndirectStore(address + 2u, asuint(placement.position.z));
        sdfIndirectStore(address + 3u, placement.classification | (passGroup.indirectEpoch << SdfIndirectEpochShift));
        [unroll] for (uint generation = 0u; generation < SdfIndirectLightingGenerations; generation++) {
            sdfIndirectStore(sdfIndirectPublicationWordOffset(passGroup.indirectTier)
                + generation * sdfIndirectProbeCapacity(passGroup.indirectTier) + index, 0u);
        }
        SdfIndirectCell empty = (SdfIndirectCell)0;
        empty.components = 0xffffffffu;
        sdfIndirectStoreCell(cell, proof, empty);
    } else {
        if (lane < SdfIndirectCellPairs) {
            bool ran = procedure != 0u;
            sdfClassifyClear[lane] = (ran && sdfIndirectPairProc.clear) ? 1u : 0u;
            sdfClassifyForward[lane] = ran ? sdfIndirectPairProc.forward : asfloat(0x7fc00000u).xxx;
            sdfClassifyReverse[lane] = ran ? sdfIndirectPairProc.reverse : asfloat(0x7fc00000u).xxx;
        }
        GroupMemoryBarrierWithGroupSync();
        if (lane == 0u) {
            SdfIndirectPlacement corners[8];
            bool clear[28];
            float3 forward[28];
            float3 reverse[28];
            [unroll] for (uint c = 0u; c < 8u; c++) { corners[c] = sdfClassifyCorners[c]; }
            [unroll] for (uint p = 0u; p < 28u; p++) {
                clear[p] = sdfClassifyClear[p] != 0u;
                forward[p] = sdfClassifyForward[p];
                reverse[p] = sdfClassifyReverse[p];
            }
            sdfIndirectStoreCell(cell, proof, sdfIndirectPartitionOfPairs(corners, clear, forward, reverse, spacing));
        }
    }
    // A probe's placement is its lane's unit; a cell's partition is its first lane's, and every lane's visits are its.
    bool unitLane = placing || lane == 0u;
    sdfIndirectReportCost(placing ? SdfIndirectCostPlace : SdfIndirectCostClassify, sdfFieldVisits + sdfIndirectLoads, unitLane ? 1u : 0u);
    uint detail = passGroup.indirectTier == SdfIndirectTierHigh ? level : level + 1u;
    sdfWorkTexels = unitLane ? 1u : 0u;
    puckCountDetail(detail, sdfWorkSteps, sdfWorkTexels, 0u, 0u, 0u);
    // A detail row holds this lane's work when the pass has detail rows; the plain row then adds none of it.
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
