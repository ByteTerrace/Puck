#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../indirect/sdf-indirect-cache.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return; }
    bool placing = passGroup.indirectPhase == 0u;
    uint count = placing ? passGroup.indirectPlaceCount : passGroup.indirectClassifyCount;
    if (group.x >= count || group.x >= sdfIndirectClassifyBudget(passGroup.indirectTier)
        || passGroup.indirectPlaceCount > sdfIndirectClassifyBudget(passGroup.indirectTier)
        || !sdfIndirectRange(0u, sdfIndirectWordCount(passGroup.indirectTier))) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint row = group.x + (placing ? 0u : passGroup.indirectPlaceCount);
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
    int3 lattice = brick.xyz * 4 + int3(lane & 3u, (lane >> 2u) & 3u, lane >> 4u);
    uint index = slot * SdfIndirectProbesPerBrick + lane;
    uint cell = sdfIndirectCellWordOffset(passGroup.indirectTier) + index * SdfIndirectCellWords;
    uint proof = sdfIndirectProofWordOffset(passGroup.indirectTier) + index * SdfIndirectProofsPerCell * SdfIndirectProofWords;
    if (placing) {
        SdfIndirectPlacement placement = sdfIndirectPlace(float3(lattice) * spacing, spacing);
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
        if (any(lattice == 2147483647)) { return; }
        SdfIndirectPlacement corners[8];
        [unroll] for (uint c = 0u; c < 8u; c++) { corners[c] = sdfIndirectReadProbe(sdfIndirectProbeIndex(lattice + sdfIndirectCorner(c), level)); }
        SdfIndirectCell partition = sdfIndirectPartition(corners, spacing);
        sdfIndirectStoreCell(cell, proof, partition);
    }
    uint detail = passGroup.indirectTier == SdfIndirectTierHigh ? level : level + 1u;
    sdfWorkTexels = 1u;
    puckCountDetail(detail, sdfWorkSteps, sdfWorkTexels, 0u, 0u, 0u);
    // A detail row holds this lane's work when the pass has detail rows; the plain row then adds none of it.
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
