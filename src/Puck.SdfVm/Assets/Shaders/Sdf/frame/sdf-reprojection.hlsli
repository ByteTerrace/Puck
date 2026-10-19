// A visible point's position in the preceding rendered view, reconstructed from the visibility record: a mesh record
// names its draw and triangle. The previous compact mesh matrices belong to the residency's last consumed frame, copied
// by SdfWorldTables.Motion.cs.
#ifndef SDF_REPROJECTION_HLSLI
#define SDF_REPROJECTION_HLSLI
#include "sdf-mesh.hlsli"
#include "sdf-visibility.hlsli"
#include "../field/sdf-quaternion.hlsli"

static const float SdfHistoryDepthTolerance = 0.05;
// Temporal color validates its receiver by identity and a relative depth tolerance.
bool sdfHistoryReceiverMatches(uint identity, uint previousIdentity, float previousT, float historyT) {
    return ((identity == previousIdentity) &&
        (abs(historyT - previousT) <= (SdfHistoryDepthTolerance * previousT)));
}

// Where a draw's index `k` stood in the preceding consumed frame: under the draw's previous matrix, or, for a vertex
// riding a member, under that member's slot's previous transform.
float3 sdfMeshPreviousPosition(uint draw, uint record, uint k) {
    float3 object = sdfMeshObjectPosition(record, k);
    uint entry = sdfMeshMemberEntry(record, sdfMeshIndexAt(record, k));

    if (entry != 0u) {
        uint slot = sdfMeshMemberSlot(record, entry);

        return (rotatePointByQuaternion(sdfMeshMemberLocal(entry, object, true), sdfPreviousDynamicTransforms[((3u * slot) + 1u)]) + sdfPreviousDynamicTransforms[(3u * slot)].xyz);
    }

    uint previousMatrix = (4u * draw);

    return ((object.x * sdfPreviousMeshTransforms[previousMatrix].xyz) +
        (object.y * sdfPreviousMeshTransforms[previousMatrix + 1u].xyz) +
        (object.z * sdfPreviousMeshTransforms[previousMatrix + 2u].xyz) + sdfPreviousMeshTransforms[previousMatrix + 3u].xyz);
}
// Returns the point carried by the same mesh triangle in the preceding consumed frame.
bool sdfPreviousPoint(uint record, SdfVisibility visibility, float3 currentPoint, out float3 previousPoint) {
    previousPoint = currentPoint;
    if (!sdfVisibilityHit(visibility)) {
        return false;
    }
    uint draw = sdfVisibilitySource(visibility.identity);
    uint mesh = sdfMeshRecord(draw);
    uint first = (3u * sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord]);
    float3 weights;
    if (!sdfMeshBarycentric(sdfMeshWorldPosition(mesh, first), sdfMeshWorldPosition(mesh, first + 1u),
        sdfMeshWorldPosition(mesh, first + 2u), currentPoint, weights)) {
        return false;
    }
    previousPoint = (((weights.x * sdfMeshPreviousPosition(draw, mesh, first)) + (weights.y * sdfMeshPreviousPosition(draw, mesh, first + 1u))) +
        (weights.z * sdfMeshPreviousPosition(draw, mesh, first + 2u)));
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
