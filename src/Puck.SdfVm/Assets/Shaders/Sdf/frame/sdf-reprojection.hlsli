// A visible point's position in the preceding rendered view, reconstructed from the visibility record. The SDF
// record names the exact winning shape slot; a mesh record names its draw and triangle. Previous rigid transforms
// and compact mesh matrices belong to the residency's last consumed frame, copied by SdfWorldTables.Motion.cs.
#ifndef SDF_REPROJECTION_HLSLI
#define SDF_REPROJECTION_HLSLI
#include "sdf-mesh.hlsli"
#include "sdf-visibility.hlsli"
#include "../field/sdf-quaternion.hlsli"

// The words one output pixel holds in the history surface the temporal resolve writes: its ray distance as a float's
// bits, then its visibility identity. KEEP IN SYNC with SdfWorldPackage.HistorySurfaceWords.
static const uint SdfHistorySurfaceWords = 2u;

// The first word of an output texel's entry in the history surface, laid out row by row at the history's extent.
uint sdfHistorySurfaceWord(uint2 texel, uint2 extent) {
    return (SdfHistorySurfaceWords * ((texel.y * extent.x) + texel.x));
}

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
// A view's camera rows, as SdfFrameBlock.WritePreviousView lays them: position and validity, right and tan(fov / 2),
// up and aspect, forward, render extent and jitter in render pixels, then the near distance and the unjittered frustum
// offset. Projects a point, given relative to the view's eye, into the view's unjittered render pixels: continuous
// coordinates with the first pixel center at (0.5, 0.5) and positive Y down. A point not beyond the near plane has none.
bool sdfProjectView(float4 rows[6], float3 relative, out float2 pixel) {
    pixel = float2(0.0, 0.0);
    float forward = dot(relative, rows[3].xyz);
    if (forward <= max(rows[5].x, 0.0)) {
        return false;
    }
    float2 tangent = (float2(dot(relative, rows[1].xyz), dot(relative, rows[2].xyz)) / forward);
    float2 ndc = ((tangent - rows[5].yz) / (float2(rows[2].w, 1.0) * rows[1].w));
    pixel = ((ndc * float2(0.5, -0.5) + 0.5) * rows[4].xy);
    return true;
}
// The preceding view's pixel positions are its jittered render coordinates: where its own sample grid sampled the
// point. The returned ray parameter is Euclidean distance along the preceding camera's normalized ray. A cut or a
// point behind that camera has no correspondence. The caller decides whether the position lies inside its history image.
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
    if (!sdfProjectView(passGroup.previousView, relative, previousPixel)) {
        return false;
    }
    previousPixel -= passGroup.previousView[4].zw;
    previousT = length(relative);
    return true;
}
#endif
