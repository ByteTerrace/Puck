// The glass pass's fragment stage, drawn with the mesh pass's vertex stage over the frame's glass draws alone: every face
// of a glass mesh runs it, front and back, since the pipeline culls nothing and the stage writes its depth, which keeps
// the depth test after it. A face before the pixel's opaque hit (the visibility record the surface pass wrote) adds its
// signed ray distance and face count to the glass sums (frame/sdf-glass.hlsli), so the sums hold the distance the ray
// travels through glass whatever order the faces rasterize in. A front face then writes the glass target as the mesh
// pass writes its target (the ray parameter, the draw plus one, the triangle) through the pass's own reversed-Z depth, so
// the nearest front face wins; a back face, or a face behind the opaque hit, writes the far plane's depth and fails the
// test. Every fragment counts one texel.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-glass.hlsli"

struct GlassFragment {
    float4 target : SV_Target0;
    float depth : SV_Depth;
};

GlassFragment PSMain(MeshVertex input, bool front : SV_IsFrontFace) {
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float t = length(input.world - view.position.xyz);
    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));
    float opaque = (sdfVisibilityHit(visibility) ? visibility.t : worldFarDistance(view));
    bool counts = (t < opaque);

    if (counts) {
        uint word = sdfGlassSumWord(pixel, passGroup.imageExtent);
        int distance = (int)round(t * SDF_GLASS_SUM_SCALE);
        uint before;

        InterlockedAdd(sdfGlassSumsRW[word], asuint(front ? -distance : distance), before);
        InterlockedAdd(sdfGlassSumsRW[(word + 1u)], asuint(front ? 1 : -1), before);
    }
    puckCountFragmentWork(0u, 1u);

    GlassFragment fragment;

    fragment.target = float4(t, float(((pushedIndex.index & SdfMeshDrawMask) + 1u)), float(input.triangleIndex), 0.0);
    fragment.depth = ((front && counts) ? input.position.z : 0.0);

    return fragment;
}
