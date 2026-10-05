#ifndef SDF_INDIRECT_BRICKS_HLSLI
#define SDF_INDIRECT_BRICKS_HLSLI
// The region keeps stable pool records first, then one sorted slot index per record. Its full-key order is
// IrradianceBrickKey.CompareTo: level, Z, Y, X. Empty directory entries sort after every placed key.
int sdfIndirectProbeIndex(int3 lattice, uint level) {
    int3 brick = int3(floor(float3(lattice) / 4.0));
    int3 local = lattice - brick * 4;
    uint capacity = sdfIndirectBrickCapacity(passGroup.indirectTier);
    uint low = 0u;
    uint high = capacity;
    [loop] while (low < high) {
        uint middle = low + (high - low) / 2u;
        int slot = indirectBricks[capacity + middle / 4u][middle % 4u];
        sdfIndirectLoads++;
        if (slot < 0) { high = middle; continue; }
        int4 entry = indirectBricks[(uint)slot];
        sdfIndirectLoads++;
        uint entryLevel = (uint)entry.w & SdfIndirectBrickLevelMask;
        if (entryLevel == level && all(entry.xyz == brick)) {
            return slot * (int)SdfIndirectProbesPerBrick + local.x + local.y * 4 + local.z * 16;
        }
        bool before = entryLevel != level ? entryLevel < level
            : entry.z != brick.z ? entry.z < brick.z
            : entry.y != brick.y ? entry.y < brick.y : entry.x < brick.x;
        if (before) { low = middle + 1u; } else { high = middle; }
    }
    return -1;
}
#endif
