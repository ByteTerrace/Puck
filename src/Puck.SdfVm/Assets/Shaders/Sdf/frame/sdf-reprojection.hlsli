// A visible point's position in the preceding rendered view, reconstructed from the visibility record: a mesh record
// names its draw and triangle. The previous mesh draw records belong to the residency's last consumed frame, copied by
// SdfWorldTables.Motion.cs.
#ifndef SDF_REPROJECTION_HLSLI
#define SDF_REPROJECTION_HLSLI
#include "sdf-mesh.hlsli"
#include "sdf-visibility.hlsli"
#include "../field/sdf-quaternion.hlsli"

static const float SdfHistoryDepthTolerance = 0.05;
// Receiver validation compares depths along the output pixel's unjittered ray. Different jittered taps on one plane
// can have very different ray distances at grazing incidence; comparing those distances rejects an unchanged plane.
// Carry the tap's tangent plane to the receiver ray, retaining the sample's distance when that ray has no finite,
// forward intersection within the view's reach.
float sdfHistoryReceiverDistance(float sampleT, float3 sampleDirection, float3 normal, float3 receiverDirection, float farDistance) {
    float facing = dot(normal, receiverDirection);
    if (abs(facing) <= 1.0e-6) {
        return sampleT;
    }
    float t = ((sampleT * dot(normal, sampleDirection)) / facing);

    return ((isfinite(t) && (t > 0.0) && (t <= farDistance)) ? t : sampleT);
}
// Temporal color validates its receiver by identity and a relative depth tolerance.
bool sdfHistoryReceiverMatches(uint identity, uint previousIdentity, float previousT, float historyT) {
    return ((identity == previousIdentity) &&
        (abs(historyT - previousT) <= (SdfHistoryDepthTolerance * previousT)));
}

// The first of a draw's pose words in the preceding consumed frame. The previous table holds compact pose words
// (SdfMeshRecord.PoseWords) rather than geometry/selection records; callers still pass the current mesh record's address.
uint sdfMeshPreviousPoseWord(uint record) {
    return ((record / SDF_MESH_DRAW_WORDS) * SDF_MESH_POSE_WORDS);
}
float3 sdfMeshPreviousPoseRow(uint word) {
    return asfloat(uint3(sdfPreviousMeshTransforms[word], sdfPreviousMeshTransforms[(word + 1u)], sdfPreviousMeshTransforms[(word + 2u)]));
}
// A draw's pose in the preceding consumed frame, as sdfMeshPoseOf reads its current one: a draw riding its frame slot from
// the slot's previous row, through the view's overlay, at its previous scale; any other from its previous matrix.
SdfMeshPose sdfMeshPreviousPoseOf(uint record) {
    uint word = sdfMeshPreviousPoseWord(record);
    int slot = asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD));
    if (slot >= 0) {
        uint shown = sdfViewPoseSlot((uint)slot);
        return sdfMeshPoseOfRow(sdfPreviousDynamicTransforms[3u * shown], sdfPreviousDynamicTransforms[3u * shown + 1u],
            asfloat(sdfPreviousMeshTransforms[word + SDF_MESH_SCALE_WORD]));
    }
    return sdfMeshPoseOfRows(sdfMeshPreviousPoseRow(word), sdfMeshPreviousPoseRow(word + 3u), sdfMeshPreviousPoseRow(word + 6u), sdfMeshPreviousPoseRow(word + 9u));
}
// A rigid draw uses the same geometry and carry in both reconstructions: its pose words and, for a draw riding its slot,
// the slot's row stand. A pose overlay, skin or displacement can move it independently of them.
bool sdfMeshDrawUnmoved(uint record) {
    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) != 0u ||
        sdfMeshDisplacementEntry(record) != SDF_FRAME_SAMPLED_NONE) return false;
    uint slot = sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD);
    if (sdfViewPoseSlot(slot) != slot) return false;
    uint word = sdfMeshPreviousPoseWord(record);
    [unroll] for (uint at = 0u; at < SDF_MESH_POSE_WORDS; at++) {
        if (sdfMeshRecordWord(record + at) != sdfPreviousMeshTransforms[word + at]) return false;
    }
    if (asint(slot) >= 0) {
        if (any(asuint(sdfDynamicTransforms[3u * slot].xyz) != asuint(sdfPreviousDynamicTransforms[3u * slot].xyz)) ||
            any(asuint(sdfDynamicTransforms[3u * slot + 1u]) != asuint(sdfPreviousDynamicTransforms[3u * slot + 1u]))) return false;
    }
    return true;
}
// Where a draw's index `k` stood in the preceding consumed frame: displaced by a height field's previous heights, skinned
// under its members' previous transforms (sdfMeshSkinPose), then under the draw's previous pose and its carry, as its
// current position is (sdfMeshWorldVertex).
float3 sdfMeshPreviousPosition(uint record, uint k) {
    SdfMeshPose pose = sdfMeshPreviousPoseOf(record);
    float3 unused = float3(0.0, 1.0, 0.0);
    float3 object = sdfMeshDisplaced(record, sdfMeshObjectPosition(record, k), true, unused);
    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) != 0u) {
        SdfMeshSkinSlots slots = sdfMeshSkinSlotsOf(record, sdfMeshIndexAt(record, k));
        object = sdfMeshSkinPose(slots, object, unused, sdfMeshPoseScale(pose),
            sdfPreviousDynamicTransforms[3u * slots.root].xyz, sdfPreviousDynamicTransforms[3u * slots.root + 1u],
            sdfPreviousDynamicTransforms[3u * slots.s0].xyz, sdfPreviousDynamicTransforms[3u * slots.s0 + 1u],
            sdfPreviousDynamicTransforms[3u * slots.s1].xyz, sdfPreviousDynamicTransforms[3u * slots.s1 + 1u]);
    }

    return sdfMeshCarryPoint(record, mul(object, pose.basis) + pose.origin);
}
// Returns the point carried by the same mesh triangle in the preceding consumed frame: the visibility record's hit (every
// hit the surface pass writes is a mesh's) resolved through the mesh hit door, which answers no previous point for a hit
// it rejects.
bool sdfPreviousPoint(uint record, SdfVisibility visibility, float3 currentPoint, out float3 previousPoint) {
    previousPoint = currentPoint;
    SdfMeshHit hit;
    if (!sdfVisibilityHit(visibility) ||
        !sdfMeshResolveHit(sdfVisibilitySource(visibility.identity), sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord], hit)) {
        return false;
    }
    uint mesh = hit.record;
    if (sdfMeshDrawUnmoved(mesh)) {
        return true;
    }
    uint first = (3u * hit.triangleIndex);
    float3 a = sdfMeshWorldPosition(mesh, first);
    float3 b = sdfMeshWorldPosition(mesh, first + 1u);
    float3 c = sdfMeshWorldPosition(mesh, first + 2u);
    float3 da = (sdfMeshPreviousPosition(mesh, first) - a);
    float3 db = (sdfMeshPreviousPosition(mesh, first + 1u) - b);
    float3 dc = (sdfMeshPreviousPosition(mesh, first + 2u) - c);

    // Interpolate displacement, not a second world position. In particular, an unchanged triangle has exactly zero
    // motion even when its long, thin outline makes barycentric reconstruction poorly conditioned.
    if (all(da == 0.0) && all(db == 0.0) && all(dc == 0.0)) {
        return true;
    }
    float3 weights;
    if (!sdfMeshBarycentric(a, b, c, currentPoint, weights)) {
        return false;
    }
    previousPoint = (currentPoint + (((weights.x * da) + (weights.y * db)) + (weights.z * dc)));
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
