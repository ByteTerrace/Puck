// One direct word read for both sparse and contiguous backing.
#ifndef SDF_MESH_WORDS_HLSLI
#define SDF_MESH_WORDS_HLSLI
#include "../isa/sdf-render.hlsli"

// A word of a draw's record (SdfMeshRecord): the record's first word (sdfMeshRecord) plus the word's offset.
uint sdfMeshRecordWord(uint word) {
    return sdfMeshRegion[word];
}
// A word of a mesh's own words, at its index in the region (an address a record or another mesh word holds).
uint sdfMeshWord(uint word) {
    return sdfMeshRegion[word];
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
