// Only a transport ray certified clear to the world's far boundary receives physical environment radiance.
// The caller binds the finite solve's pinned map; hits receive reflected prior-sweep light as Feedback instead.
#ifndef SDF_INDIRECT_SKY_HLSLI
#define SDF_INDIRECT_SKY_HLSLI
#include "../isa/sdf-indirect-layout.hlsli"
#include "../shade/sdf-sky-lighting.hlsli"

float3 sdfIndirectSky(uint kind, float3 direction, uint sources) {
    if (kind != SdfIndirectKindExit || (sources & SdfIndirectSourcesSky) == 0u) { return 0.0; }
    return sdfSkyPhysicalRadiance(direction);
}
#endif
