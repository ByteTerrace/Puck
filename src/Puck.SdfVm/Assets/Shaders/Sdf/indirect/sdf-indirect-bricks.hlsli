#ifndef SDF_INDIRECT_BRICKS_HLSLI
#define SDF_INDIRECT_BRICKS_HLSLI
uint sdfIndirectLevelCount() { return passGroup.indirectTier == SdfIndirectTierHigh ? 3u : 2u; }
// The region keeps stable pool records first, then one sorted slot index per record. Its full-key order is
// IrradianceBrickKey.CompareTo: level, Z, Y, X. Empty directory entries sort after every placed key.
int sdfIndirectProbeIndex(int3 lattice, uint level) {
    int3 brick = lattice >> 2;
    int3 local = lattice & 3;
    uint capacity = sdfIndirectBrickCapacity(passGroup.indirectTier);
    uint length, stride;
    indirectBricks.GetDimensions(length, stride);
    if (capacity == 0u || level >= sdfIndirectLevelCount() || capacity > length
        || (capacity + 3u) / 4u > length - capacity) { return -1; }
    uint low = 0u;
    uint high = capacity;
    [loop] for (uint step = 0u; step < 32u && low < high; step++) {
        uint middle = low + (high - low) / 2u;
        int brickIndex = indirectBricks[capacity + middle / 4u][middle % 4u];
        sdfIndirectLoads++;
        if (brickIndex < 0) { high = middle; continue; }
        if ((uint)brickIndex >= capacity) { return -1; }
        int4 entry = indirectBricks[(uint)brickIndex];
        sdfIndirectLoads++;
        uint entryLevel = (uint)entry.w & SdfIndirectBrickLevelMask;
        if (entryLevel == level && all(entry.xyz == brick)) {
            return brickIndex * (int)SdfIndirectProbesPerBrick + local.x + local.y * 4 + local.z * 16;
        }
        bool before = entryLevel != level ? entryLevel < level
            : entry.z != brick.z ? entry.z < brick.z
            : entry.y != brick.y ? entry.y < brick.y : entry.x < brick.x;
        if (before) { low = middle + 1u; } else { high = middle; }
    }
    return -1;
}
#endif
