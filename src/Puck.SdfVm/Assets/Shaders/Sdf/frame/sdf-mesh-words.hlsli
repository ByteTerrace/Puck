// The presentation's mesh region (sdfMeshRegion), which every residency's draw records address. One direct word read
// for both sparse and contiguous backing. A kernel that reads draw records too includes sdf-mesh-records.hlsli.
#ifndef SDF_MESH_WORDS_HLSLI
#define SDF_MESH_WORDS_HLSLI
#include "../isa/sdf-render.hlsli"

// A word of a mesh's own words, at its index in the region (an address a record or another mesh word holds). An index
// past the bound buffer reads zero: an address read from words that hold something else is wrong, and an index past
// the buffer loses the device on Vulkan, which runs without robust buffer access. A mapped Direct3D 12 region's length
// is its reserved range, whose unbacked tiles read zero already.
uint sdfMeshWord(uint word) {
    uint words;
    uint stride;

    sdfMeshRegion.GetDimensions(words, stride);
    return ((word < words) ? sdfMeshRegion[word] : 0u);
}
// Three consecutive mesh words: a vertex's position or normal, a member's rest position.
uint3 sdfMeshWords3(uint word) {
    return uint3(sdfMeshWord(word), sdfMeshWord(word + 1u), sdfMeshWord(word + 2u));
}
// Four consecutive mesh words: a member's rest orientation.
uint4 sdfMeshWords4(uint word) {
    return uint4(sdfMeshWord(word), sdfMeshWord(word + 1u), sdfMeshWord(word + 2u), sdfMeshWord(word + 3u));
}
#define sdfMeshRegion sdfMeshRegion_is_read_through_sdfMeshWord
#endif
