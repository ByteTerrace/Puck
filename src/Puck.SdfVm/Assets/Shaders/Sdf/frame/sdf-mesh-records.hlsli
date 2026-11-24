// A residency's draw records (sdfMeshDraws, SdfMeshRecord), whose word offsets address the presentation's mesh region
// (sdf-mesh-words.hlsli). The includer's interface declares both.
#ifndef SDF_MESH_RECORDS_HLSLI
#define SDF_MESH_RECORDS_HLSLI
#include "sdf-mesh-words.hlsli"

// A word of a draw's record: the record's first word (sdfMeshRecord) plus the word's offset.
uint sdfMeshRecordWord(uint word) {
    return sdfMeshDraws[word];
}
#define sdfMeshDraws sdfMeshDraws_is_read_through_sdfMeshRecordWord
#endif
