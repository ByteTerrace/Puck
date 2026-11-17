// The one door a kernel reads the mesh region through. A draw's record (sdfMeshRecordWord) is read directly from the
// region binding, sdfMeshRegion: records are each residency's own table. A mesh's own words, its vertices, triangle
// materials, indices, level table and members (sdfMeshWord), are read as SdfMeshSlabs lays them out: with
// SDF_MESH_SLAB_INDEX off, from the region binding as well; with it on, from the slab view their word's slab names
// (sdfMeshSlabs, each one slab of the same region), which is the indexed read slab backing would put in every kernel,
// a different view in each lane of a wave that shades several draws. The includer's interface declares both bindings.
// Past this file sdfMeshRegion is not a name, so a kernel that reads the region any other way does not compile.
#ifndef SDF_MESH_WORDS_HLSLI
#define SDF_MESH_WORDS_HLSLI
#include "../isa/sdf-render.hlsli"

// A word of a draw's record (SdfMeshRecord): the record's first word (sdfMeshRecord) plus the word's offset.
uint sdfMeshRecordWord(uint word) {
    return sdfMeshRegion[word];
}
// A word of a mesh's own words, at its index in the region (an address a record or another mesh word holds).
uint sdfMeshWord(uint word) {
#if SDF_MESH_SLAB_INDEX
    return sdfMeshSlabs[NonUniformResourceIndex(word >> SDF_MESH_SLAB_WORD_SHIFT)][word & ((1u << SDF_MESH_SLAB_WORD_SHIFT) - 1u)];
#else
    return sdfMeshRegion[word];
#endif
}
// Three consecutive mesh words: a vertex's position or normal, a member's rest position.
uint3 sdfMeshWords3(uint word) {
    return uint3(sdfMeshWord(word), sdfMeshWord(word + 1u), sdfMeshWord(word + 2u));
}
// Four consecutive mesh words: a member's rest orientation.
uint4 sdfMeshWords4(uint word) {
    return uint4(sdfMeshWord(word), sdfMeshWord(word + 1u), sdfMeshWord(word + 2u), sdfMeshWord(word + 3u));
}
#define sdfMeshRegion sdfMeshRegion_is_read_through_sdfMeshWord_or_sdfMeshRecordWord
#endif
