// GPU cull and level selection. Pass controls are SdfMeshOcclusion.Stage, SdfMeshCull.WriteGpu and
// SdfMeshDetail.WriteGpu. Draw metadata is the four rows at SdfMeshRecord.SelectionWord, written once per changed draw.
uint4 sdfSelectionRow(uint record, uint row) {
    uint word = record + SDF_MESH_SELECTION_WORD + 4u * row;
    return uint4(sdfMeshRegion[word], sdfMeshRegion[word + 1u], sdfMeshRegion[word + 2u], sdfMeshRegion[word + 3u]);
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
    if (kind == 2.0) {
        float4 origin = asfloat(sdfMeshOcclusionCandidates[10]);
        float4 lightRight = asfloat(sdfMeshOcclusionCandidates[11]);
        float4 lightUp = asfloat(sdfMeshOcclusionCandidates[12]);
        float3 lightForward = asfloat(sdfMeshOcclusionCandidates[13]).xyz;
        float3 relative = center - origin.xyz;
        if (!sdfSelectionWithin(lightRight.xyz, -origin.w, origin.w, relative, x, y, z) ||
            !sdfSelectionWithin(lightUp.xyz, -origin.w, origin.w, relative, x, y, z) ||
            !sdfSelectionWithin(lightForward, lightRight.w, lightUp.w, relative, x, y, z)) { return false; }
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
uint sdfSelectionLevel(uint record, uint4 previous, bool unboxed) {
    uint4 x = sdfSelectionRow(record, 1u);
    uint4 y = sdfSelectionRow(record, 2u);
    uint4 z = sdfSelectionRow(record, 3u);
    uint count = z.w;
    float4 detail = asfloat(sdfMeshOcclusionCandidates[1]);
    if (unboxed || count == 0u || detail.x == 0.0) { return 0u; }
    if (detail.x == 3.0) { return min((uint)detail.y, count); }
    uint table = sdfMeshRegion[record + SDF_MESH_LEVEL_TABLE_WORD];
    float scale = asfloat(x.w);
    uint chosen = 0u;
    if (detail.x == 2.0) {
        [loop]
        while (chosen < count && asfloat(sdfMeshRegion[table + 3u * (chosen + 1u) + 2u]) * scale <= detail.w) { chosen++; }
        return chosen;
    }
    float4 eye = asfloat(sdfMeshOcclusionCandidates[2]);
    float4 direction = asfloat(sdfMeshOcclusionCandidates[3]);
    float3 forward = direction.xyz;
    float3 center = asfloat(sdfSelectionRow(record, 0u).xyz);
    float depth = ((dot(center - eye.xyz, forward) - abs(dot(asfloat(x.xyz), forward))) - abs(dot(asfloat(y.xyz), forward))) - abs(dot(asfloat(z.xyz), forward));
    if (!(depth > 0.0)) { return 0u; }
    scale = (detail.z * scale) / depth;
    chosen = (eye.w == 0.0 && previous.z == y.w) ? min(previous.y, count) : 0u;
    // Dynamic indices read device storage, never a dynamically indexed local array.
    [loop]
    while (chosen < count && asfloat(sdfMeshRegion[table + 3u * (chosen + 1u) + 2u]) * scale <= 1.0) { chosen++; }
    [loop]
    while (chosen > 0u && asfloat(sdfMeshRegion[table + 3u * chosen + 2u]) * scale > direction.w) { chosen--; }
    return chosen;
}
uint sdfSelectionVertices(uint record, uint level) {
    uint table = sdfMeshRegion[record + SDF_MESH_LEVEL_TABLE_WORD];
    return sdfMeshRegion[table + 3u * level + 1u];
}
