// GPU cull and level selection. Pass controls are SdfMeshOcclusion.Stage, SdfMeshCull.WriteGpu and
// SdfMeshDetail.WriteGpu. Draw metadata is the four rows at SdfMeshRecord.SelectionWord, written once per changed draw.
uint4 sdfSelectionRow(uint record, uint row) {
    uint word = record + SDF_MESH_SELECTION_WORD + 4u * row;
    return uint4(sdfMeshRecordWord(word), sdfMeshRecordWord(word + 1u), sdfMeshRecordWord(word + 2u), sdfMeshRecordWord(word + 3u));
}
// A draw's world box from its selection rows (a, b, c, d): as the rows hold it, or, for a draw riding its frame slot
// (SDF_MESH_SLOT_BOX_FLAG), its object-space box through the pose and carry its vertices go through (sdfMeshPoseOf,
// sdfMeshCarryPoint), so the box is the parallelepiped the same linear map makes of the mesh's bounds and holds every
// vertex the map places, up to the rounding of the same operations, which every cull's margin covers. SdfMeshBox.Ridden is
// its host twin, which the motion scopes judge the draw by.
void sdfSelectionBox(uint record, uint4 a, uint4 b, uint4 c, uint4 d, out float3 center, out float3 x, out float3 y, out float3 z) {
    center = asfloat(a.xyz);
    x = asfloat(b.xyz);
    y = asfloat(c.xyz);
    z = asfloat(d.xyz);
    if ((a.w & SDF_MESH_SLOT_BOX_FLAG) != 0u) {
        SdfMeshPose pose = sdfMeshPoseOf(record);
        float carryScale = asfloat(sdfMeshRecordWord(record + SDF_MESH_CARRY_SCALE_WORD));
        center = sdfMeshCarryPoint(record, mul(center, pose.basis) + pose.origin);
        x = sdfMeshCarryDirection(record, mul(x, pose.basis)) * carryScale;
        y = sdfMeshCarryDirection(record, mul(y, pose.basis)) * carryScale;
        z = sdfMeshCarryDirection(record, mul(z, pose.basis)) * carryScale;
    }
}
float sdfSelectionRadius(float3 axis, float3 x, float3 y, float3 z) {
    return (abs(dot(axis, x)) + abs(dot(axis, y))) + abs(dot(axis, z));
}
bool sdfSelectionBeyond(float3 axis, float offset, float3 relative, float3 x, float3 y, float3 z) {
    return ((((dot(axis, relative) + offset) - abs(dot(axis, x))) - abs(dot(axis, y))) - abs(dot(axis, z))) >= 0.0;
}
bool sdfSelectionWithin(float3 axis, float low, float high, float3 relative, float3 x, float3 y, float3 z) {
    float middle = dot(relative, axis);
    float radius = sdfSelectionRadius(axis, x, y, z);
    return (middle + radius >= low) && (middle - radius <= high);
}
bool sdfSelectionKeeps(float3 center, float3 x, float3 y, float3 z) {
    float kind = asfloat(sdfMeshOcclusionCandidates[4]).x;
    if (kind == 0.0) { return true; }
    // Kind 3 keeps every caster in a shadow entry's light box whichever way any view faces (SdfMeshCull.OfLightBox).
    if (kind == 3.0) {
        float4 origin = asfloat(sdfMeshOcclusionCandidates[10]);
        float4 lightRight = asfloat(sdfMeshOcclusionCandidates[11]);
        float4 lightUp = asfloat(sdfMeshOcclusionCandidates[12]);
        float3 lightForward = asfloat(sdfMeshOcclusionCandidates[13]).xyz;
        float3 offset = center - origin.xyz;
        return (sdfSelectionWithin(lightRight.xyz, -origin.w, origin.w, offset, x, y, z) &&
            sdfSelectionWithin(lightUp.xyz, -origin.w, origin.w, offset, x, y, z) &&
            sdfSelectionWithin(lightForward, lightRight.w, lightUp.w, offset, x, y, z));
    }
    // Kind 1 keeps what may lie in a view's frustum before its far distance (SdfMeshCull.Of).
    float4 eye = asfloat(sdfMeshOcclusionCandidates[5]);
    float4 right = asfloat(sdfMeshOcclusionCandidates[6]);
    float4 up = asfloat(sdfMeshOcclusionCandidates[7]);
    float4 forward = asfloat(sdfMeshOcclusionCandidates[8]);
    float4 lens = asfloat(sdfMeshOcclusionCandidates[9]);
    float3 relative = center - eye.xyz;
    float vertical = right.w * (1.0 + lens.z);
    float horizontal = up.w * vertical;
    return !(sdfSelectionBeyond(-forward.xyz, forward.w, relative, x, y, z) ||
        sdfSelectionBeyond(forward.xyz, -eye.w, relative, x, y, z) ||
        sdfSelectionBeyond(right.xyz - (lens.x + horizontal) * forward.xyz, 0.0, relative, x, y, z) ||
        sdfSelectionBeyond((lens.x - horizontal) * forward.xyz - right.xyz, 0.0, relative, x, y, z) ||
        sdfSelectionBeyond(up.xyz - (lens.y + vertical) * forward.xyz, 0.0, relative, x, y, z) ||
        sdfSelectionBeyond((lens.y - vertical) * forward.xyz - up.xyz, 0.0, relative, x, y, z));
}
// The level a draw takes from its world box (sdfSelectionBox), its stretch, its staged generation and its level count.
uint sdfSelectionLevel(uint record, uint4 previous, bool unboxed, float3 center, float3 x, float3 y, float3 z, float stretch, uint generation, uint count) {
    float4 detail = asfloat(sdfMeshOcclusionCandidates[1]);
    if (unboxed || count == 0u || detail.x == 0.0) { return 0u; }
    if (detail.x == 3.0) { return min((uint)detail.y, count); }
    uint table = sdfMeshRecordWord(record + SDF_MESH_LEVEL_TABLE_WORD);
    float scale = stretch;
    uint chosen = 0u;
    if (detail.x == 2.0) {
        [loop]
        while (chosen < count && asfloat(sdfMeshWord(table + 3u * (chosen + 1u) + 2u)) * scale <= detail.w) { chosen++; }
        return chosen;
    }
    float4 eye = asfloat(sdfMeshOcclusionCandidates[2]);
    float4 direction = asfloat(sdfMeshOcclusionCandidates[3]);
    float3 forward = direction.xyz;
    float depth = ((dot(center - eye.xyz, forward) - abs(dot(x, forward))) - abs(dot(y, forward))) - abs(dot(z, forward));
    if (!(depth > 0.0)) { return 0u; }
    scale = (detail.z * scale) / depth;
    // The same staged generation validates visibility and this level history.
    chosen = (eye.w == 0.0 && previous.z == generation) ? min(previous.y, count) : 0u;
    // Dynamic indices read device storage, never a dynamically indexed local array.
    [loop]
    while (chosen < count && asfloat(sdfMeshWord(table + 3u * (chosen + 1u) + 2u)) * scale <= 1.0) { chosen++; }
    [loop]
    while (chosen > 0u && asfloat(sdfMeshWord(table + 3u * chosen + 2u)) * scale > direction.w) { chosen--; }
    return chosen;
}
uint sdfSelectionVertices(uint record, uint level) {
    uint table = sdfMeshRecordWord(record + SDF_MESH_LEVEL_TABLE_WORD);
    return sdfMeshWord(table + 3u * level + 1u);
}
