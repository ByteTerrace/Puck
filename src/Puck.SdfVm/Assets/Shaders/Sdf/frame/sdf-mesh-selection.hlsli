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
bool sdfSelectionBeyond(float3 axis, float offset, float3 relative, float3 x, float3 y, float3 z, float3 sweep, float reach) {
    float nearest = (((dot(axis, relative) + offset) - abs(dot(axis, x))) - abs(dot(axis, y))) - abs(dot(axis, z));
    nearest += min(0.0, dot(axis, sweep));
    return (nearest - reach * length(axis)) >= 0.0;
}
bool sdfSelectionWithin(float3 axis, float low, float high, float3 relative, float3 x, float3 y, float3 z) {
    float middle = dot(relative, axis);
    float radius = sdfSelectionRadius(axis, x, y, z);
    return (middle + radius >= low) && (middle - radius <= high);
}
bool sdfSelectionKeeps(float3 center, float3 x, float3 y, float3 z) {
    float kind = asfloat(sdfMeshOcclusionCandidates[4]).x;
    if (kind == 0.0) { return true; }
    float4 eye = asfloat(sdfMeshOcclusionCandidates[5]);
    float4 right = asfloat(sdfMeshOcclusionCandidates[6]);
    float4 up = asfloat(sdfMeshOcclusionCandidates[7]);
    float4 forward = asfloat(sdfMeshOcclusionCandidates[8]);
    float4 lens = asfloat(sdfMeshOcclusionCandidates[9]);
    float far = eye.w;
    float3 sweep = 0.0;
    float reach = 0.0;
    // Kind 2 is a cascade conditioned on the view's receivers, kind 3 one that keeps every caster in its light box
    // whichever way the view faces (SdfMeshCull.OfLightBox).
    if (kind >= 2.0) {
        float4 origin = asfloat(sdfMeshOcclusionCandidates[10]);
        float4 lightRight = asfloat(sdfMeshOcclusionCandidates[11]);
        float4 lightUp = asfloat(sdfMeshOcclusionCandidates[12]);
        float3 lightForward = asfloat(sdfMeshOcclusionCandidates[13]).xyz;
        float3 relative = center - origin.xyz;
        if (!sdfSelectionWithin(lightRight.xyz, -origin.w, origin.w, relative, x, y, z) ||
            !sdfSelectionWithin(lightUp.xyz, -origin.w, origin.w, relative, x, y, z) ||
            !sdfSelectionWithin(lightForward, lightRight.w, lightUp.w, relative, x, y, z)) { return false; }
        if (kind == 3.0) { return true; }
        float3 sphere = origin.xyz + lightForward * (lightUp.w - origin.w);
        float deepest = dot(sphere - eye.xyz, forward.xyz) + origin.w;
        reach = lens.w;
        far = min(far, deepest) + reach;
        sweep = lightForward * (lightUp.w - lightRight.w);
    }
    float3 relative = center - eye.xyz;
    float vertical = right.w * (1.0 + lens.z);
    float horizontal = up.w * vertical;
    return !(sdfSelectionBeyond(-forward.xyz, forward.w, relative, x, y, z, sweep, reach) ||
        sdfSelectionBeyond(forward.xyz, -far, relative, x, y, z, sweep, reach) ||
        sdfSelectionBeyond(right.xyz - (lens.x + horizontal) * forward.xyz, 0.0, relative, x, y, z, sweep, reach) ||
        sdfSelectionBeyond((lens.x - horizontal) * forward.xyz - right.xyz, 0.0, relative, x, y, z, sweep, reach) ||
        sdfSelectionBeyond(up.xyz - (lens.y + vertical) * forward.xyz, 0.0, relative, x, y, z, sweep, reach) ||
        sdfSelectionBeyond((lens.y - vertical) * forward.xyz - up.xyz, 0.0, relative, x, y, z, sweep, reach));
}
// Whether a draw's detail band keeps it in this pass (SdfMeshBand, SDF_MESH_BAND_WORD): a draw with no band always, a
// group's floor where its cell projects to at most half a display pixel over the nearest point of the band's sphere (a
// view) or to at most half a texel (a cascade), and the group's finer tier everywhere else. Both tiers of a placement
// carry the same sphere and pixel and decide here from those words and the draw's pose alone, so exactly one of them
// draws; a finer tier yields only while every record of its floor stands (SDF_MESH_ABSENT_FLAG), so a floor still
// landing never leaves a hole. A pass that takes every mesh's own triangles (detail kinds 0 and 3) and an unboxed draw
// keep the finer tier. draws is the pass's staged draw count.
bool sdfSelectionBandKeeps(uint record, bool unboxed, uint draws) {
    uint bandWord = record + SDF_MESH_BAND_WORD;
    float pixel = asfloat(sdfMeshRecordWord(bandWord + 4u));
    if (pixel == 0.0) { return true; }
    bool floorDraw = pixel < 0.0;
    float4 detail = asfloat(sdfMeshOcclusionCandidates[1]);
    bool floorStands = false;
    if (!unboxed && (detail.x == 1.0 || detail.x == 2.0)) {
        SdfMeshPose pose = sdfMeshPoseOf(record);
        float carryScale = abs(asfloat(sdfMeshRecordWord(record + SDF_MESH_CARRY_SCALE_WORD)));
        float stretch = max(length(pose.basis[0]), max(length(pose.basis[1]), length(pose.basis[2]))) * carryScale;
        float size = abs(pixel) * stretch;
        if (detail.x == 2.0) {
            floorStands = size <= detail.w;
        } else {
            float3 center = asfloat(uint3(sdfMeshRecordWord(bandWord), sdfMeshRecordWord(bandWord + 1u), sdfMeshRecordWord(bandWord + 2u)));
            float radius = asfloat(sdfMeshRecordWord(bandWord + 3u)) * stretch;
            float3 world = sdfMeshCarryPoint(record, mul(center, pose.basis) + pose.origin);
            float4 eye = asfloat(sdfMeshOcclusionCandidates[2]);
            float3 forward = asfloat(sdfMeshOcclusionCandidates[3]).xyz;
            float depth = dot(world - eye.xyz, forward) - radius;
            floorStands = (depth > 0.0) && ((size * detail.z) <= depth);
        }
    }
    if (floorDraw) { return floorStands; }
    if (!floorStands) { return true; }
    uint first = sdfMeshRecordWord(bandWord + 5u);
    uint count = sdfMeshRecordWord(bandWord + 6u);
    if (count == 0u || first >= draws || count > draws - first) { return true; }
    [loop]
    for (uint row = 0u; row < count; row++) {
        if ((sdfSelectionRow(sdfMeshRecord(first + row), 0u).w & SDF_MESH_ABSENT_FLAG) != 0u) { return true; }
    }
    return false;
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
