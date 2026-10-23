// The modules the mesh bake's kernels read: their own interface (isa/sdf-mesh-bake.interface.hlsli, generated from
// SdfWorldInterfaces.MeshBakeParameters: the World group and a pass group of the bake's values and resources alone), the
// field interpreter (field/sdf-vm.hlsli, which reads only the field's instruction set, isa/sdf-isa.hlsli) and the work
// counters. The bake samples the SDF program and writes sample records, so it reads no view, mesh table, lighting or
// screen module and no renderer constant (isa/sdf-render.hlsli): the bytecode of these kernels keys the stored meshes
// (SdfMeshBakeIdentity), and an edit to anything outside this closure leaves it byte for byte as it was.
#ifndef SDF_BAKE_WORLD_HLSLI
#define SDF_BAKE_WORLD_HLSLI
#include "../isa/sdf-mesh-bake.interface.hlsli"
#include "../field/sdf-vm.hlsli"
#include "../frame/sdf-work.hlsli"

// Where a bake lattice's instance mask starts: its mask words, then their summary words, one lattice after another.
uint worldInstanceMaskBase(uint lattice) {
    uint summaryWords = ((passGroup.instanceMaskWordCount + 31u) >> 5u);

    return ((passGroup.instanceMaskWordCount + summaryWords) * lattice);
}

#endif
