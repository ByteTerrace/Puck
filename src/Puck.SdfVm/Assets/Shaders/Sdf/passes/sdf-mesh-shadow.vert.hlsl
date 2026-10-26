// The shadow pipeline's vertex stage: the mesh pass's triangle pull (sdf-mesh.hlsli) seen from a shadow cascade's light
// camera, which SdfFrameBlock.WriteShadowCamera writes as the view: the plane's center as its position, its square half
// extent in the field-of-view slot, an aspect of one, and its depth interval as the near and far distances. The
// projection is orthographic along the light with a finite reversed-Z depth, linear from one at the near plane to zero at
// the far plane, clip = (x / h, y / h, (f - w) / (f - n), 1) for a point at plane-space right x, up y and forward distance
// w, so the cascade's depth holds each caster's light-space depth exactly as sdfMeshShadowDepth decodes it. It hands the
// fragment stage nothing but the position, and clips a caster at its draw's clip plane as the mesh pass does, so a body
// drawn clipped at a portal's glass casts only what is drawn. The pushed index carries the level of detail the cascade
// draws above the draw, as the mesh pass's does.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

struct ShadowVertex {
    float4 position : SV_Position;
    float clip : SV_ClipDistance0;
};

ShadowVertex VSMain(uint vertexId : SV_VertexID) {
    uint record = sdfMeshRecord((pushedIndex.index & SdfMeshDrawMask));
    ViewportData view = worldView();
    float3 world = sdfMeshWorldPosition(record, (vertexId + sdfMeshLevelIndex(record, (pushedIndex.index >> SDF_MESH_LEVEL_SHIFT))));
    float3 relative = (world - view.position.xyz);
    float w = dot(relative, view.forward.xyz);
    float near = worldNearDistance(view);
    float far = worldFarDistance(view);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    ShadowVertex output;

    output.position = float4((scaleX * dot(relative, view.right.xyz)), (scaleY * dot(relative, view.up.xyz)), ((far - w) / (far - near)), 1.0);
    output.clip = sdfMeshClipDistance(record, world);

    return output;
}
