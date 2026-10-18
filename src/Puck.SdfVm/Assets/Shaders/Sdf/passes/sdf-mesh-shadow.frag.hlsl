// The shadow pipeline's fragment stage: a shadow cascade keeps only the depth of the nearest caster, which the depth test
// writes from the vertex stage's orthographic position (sdf-mesh-shadow.vert.hlsl), so the stage writes no colour and
// only counts the fragment. The surface pass samples the depth (sdfMeshShadowDepth).
#include "../isa/sdf-mesh.interface.hlsli"

// The depth test runs before the stage, so a fragment it rejects never runs and never counts; each one that runs writes
// its depth texel and counts it.
[earlydepthstencil]
void PSMain(float4 position : SV_Position) {
    puckCountFragmentWork(0u, 1u);
}
