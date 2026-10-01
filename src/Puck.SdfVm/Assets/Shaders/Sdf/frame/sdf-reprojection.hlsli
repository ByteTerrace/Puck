// A visible point's position in the preceding rendered view, reconstructed from the visibility record. The SDF
// record names the exact winning shape slot; a mesh record names its draw and triangle. Previous rigid transforms
// and compact mesh matrices belong to the residency's last consumed frame, copied by SdfWorldTables.Motion.cs.
#ifndef SDF_REPROJECTION_HLSLI
#define SDF_REPROJECTION_HLSLI
#include "sdf-mesh.hlsli"
#include "sdf-visibility.hlsli"
#include "../field/sdf-quaternion.hlsli"

// Returns the point carried by the same rigid shape or mesh triangle in the preceding consumed frame.
bool sdfPreviousPoint(uint record, SdfVisibility visibility, float3 currentPoint, out float3 previousPoint) {
    previousPoint = currentPoint;
    if (!sdfVisibilityHit(visibility)) {
        return false;
    }
    if (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_MESH) {
        uint draw = sdfVisibilitySource(visibility.identity);
        uint mesh = sdfMeshRecord(draw);
        uint first = (3u * sdfVisibilityMeshTriangle(record));
        float3 weights;
        if (!sdfMeshBarycentric(sdfMeshWorldPosition(mesh, first), sdfMeshWorldPosition(mesh, first + 1u),
            sdfMeshWorldPosition(mesh, first + 2u), currentPoint, weights)) {
            return false;
        }
        float3 local = ((weights.x * sdfMeshObjectPosition(mesh, first)) +
            (weights.y * sdfMeshObjectPosition(mesh, first + 1u)) + (weights.z * sdfMeshObjectPosition(mesh, first + 2u)));
        uint previousMatrix = (4u * draw);
        previousPoint = ((local.x * sdfPreviousMeshTransforms[previousMatrix].xyz) +
            (local.y * sdfPreviousMeshTransforms[previousMatrix + 1u].xyz) +
            (local.z * sdfPreviousMeshTransforms[previousMatrix + 2u].xyz) + sdfPreviousMeshTransforms[previousMatrix + 3u].xyz);
        return true;
    }
    int slot = sdfVisibilityFrameSlot(record, visibility);
    if (slot != SDF_TRANSFORM_SLOT_NONE) {
        uint row = (3u * (uint)slot);
        float3 local = rotatePointByInverseQuaternion(currentPoint - sdfDynamicTransforms[row].xyz, sdfDynamicTransforms[row + 1u]);
        previousPoint = (rotatePointByQuaternion(local, sdfPreviousDynamicTransforms[row + 1u]) + sdfPreviousDynamicTransforms[row].xyz);
    }
    return true;
}
// Pixel positions use continuous render coordinates, with the first pixel center at (0.5, 0.5), and positive Y down.
// The returned ray parameter is Euclidean distance along the preceding camera's normalized ray. A cut or a point
// behind that camera has no correspondence. The caller decides whether the position lies inside its history image.
bool sdfReprojection(uint record, float3 currentPoint, out float2 previousPixel, out float previousT) {
    previousPixel = float2(0.0, 0.0);
    previousT = 0.0;
    if (passGroup.previousView[0].w == 0.0) {
        return false;
    }
    float3 previousPoint;
    if (!sdfPreviousPoint(record, sdfLoadVisibility(record), currentPoint, previousPoint)) {
        return false;
    }
    float3 relative = (previousPoint - passGroup.previousView[0].xyz);
    float forward = dot(relative, passGroup.previousView[3].xyz);
    if (forward <= max(passGroup.previousView[5].x, 0.0)) {
        return false;
    }
    float2 tangent = (float2(dot(relative, passGroup.previousView[1].xyz), dot(relative, passGroup.previousView[2].xyz)) / forward);
    float2 ndc = ((tangent - passGroup.previousView[5].yz) /
        (float2(passGroup.previousView[2].w, 1.0) * passGroup.previousView[1].w));
    previousPixel = ((ndc * float2(0.5, -0.5) + 0.5) * passGroup.previousView[4].xy);
    previousT = length(relative);
    return true;
}
#endif
