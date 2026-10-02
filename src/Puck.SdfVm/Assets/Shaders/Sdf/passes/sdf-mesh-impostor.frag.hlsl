// The impostor card pipeline's fragment stage: the sdf-mesh.frag.hlsl of a card. A card pixel is a pixel the draw's bounding
// sphere covers, not one the prototype does, so the stage finds the surface the camera's ray meets in the impostor's views
// (frame/sdf-mesh-impostor.hlsli) and discards the pixel when it meets none. A pixel it keeps writes the same three values
// the mesh stage writes: the ray parameter of that surface (the Euclidean distance from the camera), the draw plus one, and
// the card's triangle, and the depth of that surface, so the surface sorts against the other meshes as it stands in the
// scene, not as the card does. The card stands on the plane that touches the sphere's near side, so the surface is never
// nearer than the card: the stage promises its depth is no greater than the card's (SV_DepthLessEqual, reversed Z), which
// keeps the depth test's early rejection of pixels behind a nearer surface. A draw whose impostor the atlases do not hold
// is not a card, and discards every pixel.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-impostor.hlsli"

// What the vertex stage hands this stage (MeshVertex), with the position interpolated in screen space and at the pixel's
// centroid, which Direct3D 12 requires of a stage that outputs a conservative depth.
struct CardInput {
    noperspective centroid float4 position : SV_Position;
    float3 world : WORLD;
    nointerpolation uint triangleIndex : MESHTRIANGLE;
};
struct CardFragment {
    float4 color : SV_Target0;
    float depth : SV_DepthLessEqual;
};

CardFragment PSMain(CardInput input) {
    ViewportData view = worldView();
    uint draw = (pushedIndex.index & SdfMeshDrawMask);
    float3 offset = (input.world - view.position.xyz);
    float3 direction = normalize(offset);
    float t;

    if (
        !sdfMeshIsImpostor(draw) ||
        !sdfImpostorTrace(draw, view.position.xyz, direction, (2.0 * view.right.w / view.extent.y), t)
    ) {
        discard;
    }

    puckCountFragmentWork(0u, 1u);

    CardFragment output;
    float forward = (t * dot(direction, view.forward.xyz));

    output.color = float4(t, float(draw + 1u), float(input.triangleIndex), 0.0);
    output.depth = min((worldSurfaceNearDistance(view) / forward), input.position.z);

    return output;
}
