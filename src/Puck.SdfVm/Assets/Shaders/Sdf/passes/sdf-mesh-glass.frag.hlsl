// The glass pass's fragment stage, drawn with the mesh pass's vertex stage over the frame's glass draws alone: every face
// of a glass mesh runs it, front and back, since the pipeline culls nothing and the stage writes its depth, which keeps
// the depth test after it. A face before the pixel's opaque hit (the visibility record the surface pass wrote) adds its
// signed ray distance and face count to the glass sums (frame/sdf-glass.hlsli), so the sums hold the distance the ray
// travels through glass whatever order the faces rasterize in. A front face then writes the glass target as the mesh
// pass writes its target (the ray parameter, the draw plus one, the triangle) through the pass's own reversed-Z depth, so
// the nearest front face wins; a back face, or a face behind the opaque hit, writes the far plane's depth and fails the
// test. Every fragment counts one texel. A face is a front face when its triangle's authored outward normal (the bake's
// winding, which curvature also reads) faces the camera; the rasterizer's facing is not used, since its sense follows each
// backend's winding and viewport conventions rather than the mesh.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-raster.hlsli"
#include "../frame/sdf-stamp-fade.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-glass.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"

// Presentation floats avoid overflow when a thin, strongly absorbing material lies far from the opaque hit.
// Each successful compare/exchange contributes once; helper lanes must not enter an atomic retry loop.
void sdfGlassAddOpticalDepth(uint word, float value) {
    uint expected = sdfGlassSumsRW[word];
    [allow_uav_condition] while (true) {
        uint observed;
        InterlockedCompareExchange(sdfGlassSumsRW[word], expected, asuint(asfloat(expected) + value), observed);
        if (observed == expected) { break; }
        expected = observed;
    }
}

struct GlassFragment {
    float4 target : SV_Target0;
    float depth : SV_Depth;
};

GlassFragment PSMain(MeshRasterVertex input) {
    sdfStampFade(input.draw, uint2(input.position.xy));
    ViewportData view = worldView();
    uint2 pixel = uint2(input.position.xy);
    float t = length(input.world - view.position.xyz);
    uint record = sdfMeshRecord(input.draw);
    uint first = (3u * input.triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    bool front = (dot(cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a)), (input.world - view.position.xyz)) < 0.0);
    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent));
    float opaque = (sdfVisibilityHit(visibility) ? visibility.t : worldFarDistance(view));
    bool counts = (t < opaque);

    if (counts && !IsHelperLane()) {
        uint word = sdfGlassSumWord(pixel, passGroup.imageExtent);
        int distance = (int)round(t * SDF_GLASS_SUM_SCALE);
        uint before;

        InterlockedAdd(sdfGlassSumsRW[word], asuint(front ? -distance : distance), before);
        InterlockedAdd(sdfGlassSumsRW[(word + 1u)], asuint(front ? 1 : -1), before);
        // Integrate each material independently up to the opaque hit. Paired faces subtract out the air between
        // solids; an opaque surface inside a solid clips its interval without needing a back face behind it.
        SdfMaterialData material = sdfMaterialLoad(sdfMeshMaterial(input.draw, input.triangleIndex));
        float3 opticalDepth = material.absorption * (opaque - t);
        if (!front) { opticalDepth = -opticalDepth; }
        sdfGlassAddOpticalDepth(word + 2u, opticalDepth.x);
        sdfGlassAddOpticalDepth(word + 3u, opticalDepth.y);
        sdfGlassAddOpticalDepth(word + 4u, opticalDepth.z);
    }
    puckCountFragmentWork(0u, 1u);

    GlassFragment fragment;

    fragment.target = float4(t, float(input.draw + 1u), float(input.triangleIndex), 0.0);
    fragment.depth = ((front && counts) ? input.position.z : 0.0);

    return fragment;
}
