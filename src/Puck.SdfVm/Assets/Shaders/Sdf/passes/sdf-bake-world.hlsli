// The modules the mesh bake's kernels read: the field interpreter (field/sdf-vm.hlsli) and the work counters. The bake
// samples the SDF program and writes sample records, so it reads no view, mesh table, lighting or screen module: an edit
// to one of those must not recompile it, and the bytecode of these kernels keys the stored meshes (SdfMeshBakeIdentity).
#ifndef SDF_BAKE_WORLD_HLSLI
#define SDF_BAKE_WORLD_HLSLI
#include "../field/sdf-vm.hlsli"
#include "../frame/sdf-work.hlsli"

// Where a bake lattice's instance mask starts: its mask words, then their summary words, one lattice after another.
uint worldInstanceMaskBase(uint lattice) {
    uint summaryWords = ((passGroup.instanceMaskWordCount + 31u) >> 5u);

    return ((passGroup.instanceMaskWordCount + summaryWords) * lattice);
}

#endif
