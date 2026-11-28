// A draw's world box as its record's selection rows hold it (SdfMeshRecord.SelectionWord, written once per changed draw):
// the one reader of the box for the selection kernel (frame/sdf-mesh-selection.hlsli) and the glass raster
// (passes/sdf-mesh-glass.frag.hlsl).
#ifndef FRAME_SDF_MESH_BOX_HLSLI
#define FRAME_SDF_MESH_BOX_HLSLI

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
// The reach of a box of half axes (x, y, z) along `axis`, a unit vector: its half extent along it.
float sdfSelectionRadius(float3 axis, float3 x, float3 y, float3 z) {
    return (abs(dot(axis, x)) + abs(dot(axis, y))) + abs(dot(axis, z));
}
#endif
