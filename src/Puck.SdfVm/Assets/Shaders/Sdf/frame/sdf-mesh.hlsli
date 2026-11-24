// The mesh pass's and the surface and views passes' reads of a residency's draw records and the presentation's mesh
// region, SdfMeshRegion's raw word layout: one record a draw (SdfMeshRecord) in the draw records, and in the region each
// mesh's vertices, six words each (position, normal), then its triangle materials, then its indices, then its members.
// Every offset and flag is SdfMeshRecord's (Puck.SdfVm.Model), generated into sdf-render.hlsli (SDF_MESH_*). The
// includer's interface declares the draw records, sdfMeshDraws, the region, sdfMeshRegion, the frame's dynamic
// transforms, sdfDynamicTransforms, and the frame's sampled table, sdfFrameSampled: sdf-world and sdf-mesh all do.
// A record's words are read through sdfMeshRecordWord (sdf-mesh-records.hlsli) and a mesh's own words through
// sdfMeshWord (sdf-mesh-words.hlsli). A draw's placement passes through sdfMeshPoseOf, which poses a draw riding its frame
// slot from the slot's row and any other from the matrix its record holds. Every read of a vertex's position or normal
// passes through sdfMeshWorldPosition, sdfMeshWorldNormal or, for the previous frame, sdfMeshPreviousPosition, which
// displace a height field's draw (sdfMeshDisplaced), so every pass sees one surface.
#ifndef SDF_MESH_HLSLI
#define SDF_MESH_HLSLI
#include "../field/sdf-quaternion.hlsli"
#include "../isa/sdf-render.hlsli"
#include "sdf-mesh-records.hlsli"

// The bits below the level of detail (SDF_MESH_LEVEL_SHIFT) in the index a mesh draw call pushes: the draw.
static const uint SdfMeshDrawMask = ((1u << SDF_MESH_LEVEL_SHIFT) - 1u);

// The first word of a draw's record.
uint sdfMeshRecord(uint draw) {
    return (draw * SDF_MESH_DRAW_WORDS);
}
// The first index of a draw's level of detail: zero for its mesh's own triangles, otherwise three times the level's first
// triangle among the mesh's levelled triangles (SDF_MESH_LEVEL_TABLE_WORD). A draw of the level adds it to its vertex
// id, which counts from zero on every backend, so the triangle a vertex names is the level's.
uint sdfMeshLevelIndex(uint record, uint level) {
    if (level == 0u) { return 0u; }
    uint table = sdfMeshRecordWord(record + SDF_MESH_LEVEL_TABLE_WORD);
    uint count = sdfMeshRecordWord(record + SDF_MESH_LEVEL_TABLE_WORD + 1u);
    return 3u * sdfMeshWord(table + 3u * min(level, count));
}

// A hit a pass read from GPU data (a mesh or glass target's texel, a visibility record, the glass sums' front records),
// resolved through the mesh hit door (sdfMeshResolveHit): the first word of its draw's record and its triangle among the
// mesh's levelled triangles, so its indices begin at three times it. Every reader of a hit's record, indices or triangle
// material takes one, so a read that skipped the door does not compile; HLSL has no private constructor, so a kernel
// filling one by hand is review's to catch. The raster stages index the draw the selection kernel emitted and a
// triangle its own vertex ids name, and read through sdfMeshRecord and sdfMeshRasterMaterial instead.
struct SdfMeshHit {
    uint record;
    uint triangleIndex;
};

// The mesh hit door. A hit is accepted when its draw is below the frame's staged draws (passGroup.meshDraws) and its
// triangle lies among its mesh's levelled triangles: three times their count is the record's level table word less its
// index word (SdfMeshRegion.WriteDraw), which also bounds its triangle materials. Anything else (a target written under
// other records, a record past the staged extent, a record whose placement words are zero) is a miss, never a triangle
// that is in bounds and wrong, and is counted (gpu.mesh-hits.rejected, puckCountRejectedHit).
bool sdfMeshResolveHit(uint draw, uint triangleIndex, out SdfMeshHit hit) {
    hit.record = 0u;
    hit.triangleIndex = 0u;
    if (draw < passGroup.meshDraws) {
        uint record = sdfMeshRecord(draw);
        uint indexWord = sdfMeshRecordWord(record + SDF_MESH_INDEX_WORD);
        uint levelTable = sdfMeshRecordWord(record + SDF_MESH_LEVEL_TABLE_WORD);

        if ((levelTable >= indexWord) && (triangleIndex < ((levelTable - indexWord) / 3u))) {
            hit.record = record;
            hit.triangleIndex = triangleIndex;
            return true;
        }
    }
    puckCountRejectedHit();
    return false;
}

// The material a record's triangle shades with: the draw's material, plus the triangle's palette entry when its mesh
// carries one. Read only through sdfMeshMaterial (a resolved hit) and sdfMeshRasterMaterial (a raster stage's own draw).
int sdfMeshMaterialOf(uint record, uint triangleIndex) {
    int material = asint(sdfMeshRecordWord(record + SDF_MESH_MATERIAL_WORD));

    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_MATERIALS_FLAG) != 0u) {
        material += asint(sdfMeshWord(sdfMeshRecordWord(record + SDF_MESH_TRIANGLE_MATERIAL_WORD) + triangleIndex));
    }

    return material;
}
// The material a resolved hit shades with.
int sdfMeshMaterial(SdfMeshHit hit) {
    return sdfMeshMaterialOf(hit.record, hit.triangleIndex);
}
// The material a raster stage's triangle shades with: the draw the selection kernel emitted, and the triangle the draw's
// own vertex ids name. No other stage calls it.
int sdfMeshRasterMaterial(uint draw, uint triangleIndex) {
    return sdfMeshMaterialOf(sdfMeshRecord(draw), triangleIndex);
}
// The row this view reads a dynamic-transform slot from. A view's pose overlay (passGroup.poseOverlay: first slot, slot
// count, first overlay row; SdfPoseOverlay) reads one stamp's slots from the overlay's rows, which hold that stamp posed
// for this view alone; every other slot, and every slot of a view with no overlay (a zero count), reads its own row. A
// negative slot (SDF_TRANSFORM_SLOT_NONE) wraps far past any range and reads as itself. Every reader of a mesh vertex's
// slots (sdfMeshSkinSlotsOf, sdfMeshPoseOf, sdfMeshPreviousPoseOf) and of a hit's shading frame (sdfMeshHitFrameSlot,
// sdfMeshSurfaceNormal) goes through it.
uint sdfViewPoseSlot(uint slot) {
    uint offset = (slot - passGroup.poseOverlay.x);
    return ((offset < passGroup.poseOverlay.y) ? (passGroup.poseOverlay.z + offset) : slot);
}
// A draw's placement before its carry: the rows an object-space point goes through (x * basis + origin, the row-vector
// convention), and the rows an object-space normal goes through (the inverse transpose of the basis, up to a positive
// factor for a draw riding its slot, whose normals every reader normalizes).
struct SdfMeshPose {
    float3x3 basis;
    float3 origin;
    float3x3 normals;
};
// The pose a slot's row places a draw riding it in, at the draw's scale: the scale first, then the row's orientation
// and position, as SdfMeshBox.Ridden places its box on the host. The orientation is read as the row holds it, so an
// unset one is the identity.
SdfMeshPose sdfMeshPoseOfRow(float4 position, float4 orientation, float scale) {
    float3 x = rotatePointByQuaternion(float3(1.0, 0.0, 0.0), orientation);
    float3 y = rotatePointByQuaternion(float3(0.0, 1.0, 0.0), orientation);
    float3 z = rotatePointByQuaternion(float3(0.0, 0.0, 1.0), orientation);
    SdfMeshPose pose;
    pose.basis = float3x3(x * scale, y * scale, z * scale);
    pose.origin = position.xyz;
    pose.normals = float3x3(x, y, z);
    return pose;
}
// The pose an affine matrix's rows place a draw in, normals going through the inverse transpose of its basis, the
// cofactor rows over the determinant; a singular basis, which draws no area, carries normals by itself.
SdfMeshPose sdfMeshPoseOfRows(float3 r0, float3 r1, float3 r2, float3 origin) {
    SdfMeshPose pose;
    pose.basis = float3x3(r0, r1, r2);
    pose.origin = origin;
    float3 c0 = cross(r1, r2);
    float3 c1 = cross(r2, r0);
    float3 c2 = cross(r0, r1);
    float determinant = dot(r0, c0);
    if (determinant != 0.0) {
        pose.normals = float3x3(c0, c1, c2) * (1.0 / determinant);
    } else {
        pose.normals = pose.basis;
    }
    return pose;
}
// Three of a pose word table's words as a float3, from `word`.
float3 sdfMeshPoseRow(uint word) {
    return asfloat(uint3(sdfMeshRecordWord(word), sdfMeshRecordWord(word + 1u), sdfMeshRecordWord(word + 2u)));
}
// A draw's pose in this view. A draw riding its frame slot (SDF_MESH_FRAME_SLOT_WORD at or above zero) is posed by the
// row the view reads the slot from (sdfViewPoseSlot) at the scale its record holds (SDF_MESH_SCALE_WORD), so it moves
// as its slot's row moves and its record never does; any other by the affine matrix its pose words hold, read from the
// record alone.
SdfMeshPose sdfMeshPoseOf(uint record) {
    int slot = asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD));
    if (slot >= 0) {
        uint shown = sdfViewPoseSlot((uint)slot);
        return sdfMeshPoseOfRow(sdfDynamicTransforms[3u * shown], sdfDynamicTransforms[3u * shown + 1u],
            asfloat(sdfMeshRecordWord(record + SDF_MESH_SCALE_WORD)));
    }
    return sdfMeshPoseOfRows(sdfMeshPoseRow(record), sdfMeshPoseRow(record + 3u), sdfMeshPoseRow(record + 6u), sdfMeshPoseRow(record + 9u));
}
// The uniform scale a pose takes an object-space length by: its basis's first row's length.
float sdfMeshPoseScale(SdfMeshPose pose) {
    return length(pose.basis[0]);
}
// The dynamic-transform slot whose row a resolved hit's draw shades in this view (sdfViewPoseSlot), or
// SDF_TRANSFORM_SLOT_NONE.
int sdfMeshHitFrameSlot(SdfMeshHit hit) {
    return asint(sdfViewPoseSlot(sdfMeshRecordWord(hit.record + SDF_MESH_FRAME_SLOT_WORD)));
}
// The vertex a draw's index `k` names.
uint sdfMeshIndexAt(uint record, uint k) {
    return sdfMeshWord(sdfMeshRecordWord(record + SDF_MESH_INDEX_WORD) + k);
}
// The first word of the vertex a draw's index `k` names.
uint sdfMeshVertexAt(uint record, uint k) {
    return (sdfMeshRecordWord(record + SDF_MESH_VERTEX_WORD) + (SDF_MESH_VERTEX_WORDS * sdfMeshIndexAt(record, k)));
}
// The first of a skinned vertex's SDF_MESH_SKIN_VERTEX_WORDS words (SdfMeshRecord.SkinVertexWords): its first member,
// then its second member and share, its gradient's X and Y, and its gradient's Z (SdfMeshInfluence).
uint sdfMeshInfluenceWord(uint record, uint vertex) {
    uint skin = sdfMeshRecordWord(record + SDF_MESH_SKIN_WORD);
    return skin + 1u + SDF_MESH_MEMBER_WORDS * sdfMeshWord(skin) + SDF_MESH_SKIN_VERTEX_WORDS * vertex;
}
// The first word of the member at `index` of a skinned draw (its slot offset, then its baked position and orientation),
// or zero for the root sentinel, which rides the draw's matrix.
uint sdfMeshMemberEntryOf(uint record, uint index) {
    uint skin = sdfMeshRecordWord(record + SDF_MESH_SKIN_WORD);
    return index == 255u ? 0u : skin + 1u + SDF_MESH_MEMBER_WORDS * index;
}
// The member a draw's vertex rides first, or zero for a vertex that rides the draw's matrix: a baked group's shape on a
// slot of its own moves its vertices as that slot moves, from where it stood when the mesh was baked (SdfMesh.Members).
// A vertex wholly one owner's rides that owner first with no share (SdfMeshSkinTuple.Of).
uint sdfMeshMemberEntry(uint record, uint vertex) {
    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) == 0u) {
        return 0u;
    }
    return sdfMeshMemberEntryOf(record, sdfMeshWord(sdfMeshInfluenceWord(record, vertex)));
}
// The slot a member rides: the draw's frame slot plus the member's offset.
uint sdfMeshMemberSlot(uint record, uint entry) {
    return (uint)(asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD)) + asint(sdfMeshWord(entry)));
}
// A direction in a draw's object space, under its pose's basis without the translation (v * M, the row-vector
// convention).
float3 sdfMeshWorldDirection(uint record, float3 v) {
    return mul(v, sdfMeshPoseOf(record).basis);
}
// The object position of the vertex a draw's index `k` names.
float3 sdfMeshObjectPosition(uint record, uint k) {
    return asfloat(sdfMeshWords3(sdfMeshVertexAt(record, k)));
}
// A world direction under the draw's carry rotation (SDF_MESH_CARRY_ROTATION_WORD).
float3 sdfMeshCarryDirection(uint record, float3 v) {
    uint word = (record + SDF_MESH_CARRY_ROTATION_WORD);

    return rotatePointByQuaternion(v, asfloat(uint4(sdfMeshRecordWord(word), sdfMeshRecordWord(word + 1u), sdfMeshRecordWord(word + 2u), sdfMeshRecordWord(word + 3u))));
}
// A world point under the draw's carry: its rotation, its scale (SDF_MESH_CARRY_SCALE_WORD), then its translation
// (SDF_MESH_CARRY_TRANSLATION_WORD). The carry is the last step of every world position the draw resolves, so a copy of a
// body drawn through a portal pair is the body, moved and scaled through the pair.
float3 sdfMeshCarryPoint(uint record, float3 p) {
    uint word = (record + SDF_MESH_CARRY_TRANSLATION_WORD);

    return ((sdfMeshCarryDirection(record, p) * asfloat(sdfMeshRecordWord(record + SDF_MESH_CARRY_SCALE_WORD))) + asfloat(uint3(sdfMeshRecordWord(word), sdfMeshRecordWord(word + 1u), sdfMeshRecordWord(word + 2u))));
}
// A world point's signed distance from the draw's clip plane (SDF_MESH_CLIP_PLANE_WORD): negative where the draw is not
// drawn, zero everywhere for a draw that clips nothing. The mesh and shadow vertex stages hand it on as SV_ClipDistance0.
float4 sdfMeshClipPlane(uint record) {
    uint word = (record + SDF_MESH_CLIP_PLANE_WORD);
    return asfloat(uint4(sdfMeshRecordWord(word), sdfMeshRecordWord(word + 1u), sdfMeshRecordWord(word + 2u), sdfMeshRecordWord(word + 3u)));
}
float sdfMeshClipDistance(uint record, float3 world) {
    float4 plane = sdfMeshClipPlane(record);

    return (dot(plane.xyz, world) + plane.w);
}
// The frame's sampled-table entry of the heights a draw is displaced by (SdfFrameSampled), or SDF_FRAME_SAMPLED_NONE
// for a draw drawn as it was baked.
uint sdfMeshDisplacementEntry(uint record) {
    uint handle = sdfMeshRecordWord(record + SDF_MESH_DISPLACEMENT_WORD);

    return ((handle == SDF_FRAME_SAMPLED_NONE) ? SDF_FRAME_SAMPLED_NONE : sdfFrameSampled[handle]);
}
// A height field's height above its origin at a point's x and z, and its gradient along x and z, in world units: the
// bilinear height over the four nearest column centres, the frame's heights or the previous frame's, weighed over the
// raised columns alone (height above zero) so a level holds to the edge of the raised columns; the point's own column's
// height, with no slope, where none of the four is raised. Columns past the lattice's edge read its edge's.
float sdfMeshHeightAt(uint entry, bool previous, float2 point2, out float2 gradient) {
    uint width = sdfFrameSampled[entry];
    uint depth = sdfFrameSampled[(entry + 1u)];
    float2 origin = asfloat(uint2(sdfFrameSampled[(entry + 2u)], sdfFrameSampled[(entry + 4u)]));
    float cell = asfloat(sdfFrameSampled[(entry + 5u)]);
    uint values = ((entry + SDF_FRAME_SAMPLED_HEADER_WORDS) + (previous ? (width * depth) : 0u));
    float2 grid = (((point2 - origin) / cell) - 0.5);
    float2 corner = floor(grid);
    float2 f = (grid - corner);
    int2 low = int2(corner);
    uint2 last = uint2((width - 1u), (depth - 1u));
    uint2 c0 = uint2(clamp(low, int2(0, 0), int2(last)));
    uint2 c1 = uint2(clamp((low + int2(1, 1)), int2(0, 0), int2(last)));
    float h00 = asfloat(sdfFrameSampled[((values + (c0.y * width)) + c0.x)]);
    float h10 = asfloat(sdfFrameSampled[((values + (c0.y * width)) + c1.x)]);
    float h01 = asfloat(sdfFrameSampled[((values + (c1.y * width)) + c0.x)]);
    float h11 = asfloat(sdfFrameSampled[((values + (c1.y * width)) + c1.x)]);
    float4 raised = float4((h00 > 0.0), (h10 > 0.0), (h01 > 0.0), (h11 > 0.0));
    float4 weights = (float4(((1.0 - f.x) * (1.0 - f.y)), (f.x * (1.0 - f.y)), ((1.0 - f.x) * f.y), (f.x * f.y)) * raised);
    float4 weightsX = (float4(-(1.0 - f.y), (1.0 - f.y), -f.y, f.y) * raised);
    float4 weightsZ = (float4(-(1.0 - f.x), -f.x, (1.0 - f.x), f.x) * raised);
    float4 heights = float4(h00, h10, h01, h11);
    float total = dot(weights, float4(1.0, 1.0, 1.0, 1.0));

    gradient = float2(0.0, 0.0);
    if (total <= 0.0) {
        uint2 own = uint2(clamp(int2(round(grid)), int2(0, 0), int2(last)));

        return max(asfloat(sdfFrameSampled[((values + (own.y * width)) + own.x)]), 0.0);
    }

    float sum = dot(weights, heights);
    float height = (sum / total);

    gradient = float2(
        ((dot(weightsX, heights) - (height * dot(weightsX, float4(1.0, 1.0, 1.0, 1.0)))) / total),
        ((dot(weightsZ, heights) - (height * dot(weightsZ, float4(1.0, 1.0, 1.0, 1.0)))) / total)
    ) / cell;

    return height;
}
// A height field's draw's object-space point displaced by its heights (SdfHeightField), and the normal there carried
// through the displacement: the reference was baked with every raised column's top SDF_HEIGHT_FIELD_REFERENCE_CELLS
// cells above the origin and its base a cell below it. The top band, from a cell under the reference top up, which holds
// the top face and its edge rounding, rises by the height less the reference; the band below scales from the base, so
// walls stay vertical. A normal goes through the map's inverse transpose (its parts along x and z tilted by the map's
// slope), which on the top face is the height's gradient. A draw that is not displaced returns the point and normal as
// they are.
float3 sdfMeshDisplaced(uint record, float3 p, bool previous, inout float3 normal) {
    uint entry = sdfMeshDisplacementEntry(record);

    if (entry == SDF_FRAME_SAMPLED_NONE) { return p; }

    float originY = asfloat(sdfFrameSampled[(entry + 3u)]);
    float cell = asfloat(sdfFrameSampled[(entry + 5u)]);
    float reference = (SDF_HEIGHT_FIELD_REFERENCE_CELLS * cell);
    float base = (originY - cell);
    float split = ((originY + reference) - cell);
    float2 gradient;
    float height = sdfMeshHeightAt(entry, previous, p.xz, gradient);
    float slopeY = 1.0;
    float2 slopeXZ = gradient;

    if (p.y >= split) {
        p.y += (height - reference);
    } else {
        float along = ((p.y - base) / reference);

        slopeY = (height / reference);
        slopeXZ = (gradient * along);
        p.y = (base + (along * height));
    }
    normal = float3(((normal.x * slopeY) - (normal.y * slopeXZ.x)), normal.y, ((normal.z * slopeY) - (normal.y * slopeXZ.y)));

    return p;
}
#include "sdf-mesh-skin.hlsli"
// The world position and normal of a draw's index `k`: the vertex it names displaced by a height field's heights
// (sdfMeshDisplaced), skinned under its members' slots in this frame (sdfMeshSkinCurrent), then under the draw's pose in
// this view (sdfMeshPoseOf: its basis and origin, the normal through its normal rows), and its portal carry. The normal
// is meaningful only for a mesh that carries normals.
void sdfMeshWorldVertex(uint record, uint k, out float3 position, out float3 normal) {
    SdfMeshPose pose = sdfMeshPoseOf(record);
    normal = asfloat(sdfMeshWords3(sdfMeshVertexAt(record, k) + 3u));
    float3 object = sdfMeshDisplaced(record, sdfMeshObjectPosition(record, k), false, normal);
    object = sdfMeshSkinCurrent(record, k, object, normal, sdfMeshPoseScale(pose));
    position = sdfMeshCarryPoint(record, mul(object, pose.basis) + pose.origin);
    normal = sdfMeshCarryDirection(record, mul(normal, pose.normals));
}
float3 sdfMeshWorldPosition(uint record, uint k) {
    float3 position, normal;
    sdfMeshWorldVertex(record, k, position, normal);
    return position;
}
float3 sdfMeshWorldNormal(uint record, uint k) {
    float3 position, normal;
    sdfMeshWorldVertex(record, k, position, normal);
    return normal;
}
// The barycentric weights of a point on the triangle (a, b, c), in that order, or false for a triangle with no area.
bool sdfMeshBarycentric(float3 a, float3 b, float3 c, float3 p, out float3 weights) {
    float3 ab = (b - a);
    float3 ac = (c - a);
    float3 ap = (p - a);
    float d00 = dot(ab, ab);
    float d01 = dot(ab, ac);
    float d11 = dot(ac, ac);
    float d20 = dot(ap, ab);
    float d21 = dot(ap, ac);
    float denominator = ((d00 * d11) - (d01 * d01));

    weights = float3((1.0 / 3.0), (1.0 / 3.0), (1.0 / 3.0));

    if (abs(denominator) <= 1.0e-20) {
        return false;
    }

    float v = (((d11 * d20) - (d01 * d21)) / denominator);
    float w = (((d00 * d21) - (d01 * d20)) / denominator);

    weights = float3(((1.0 - v) - w), v, w);

    return true;
}
// A surface normal of a triangle whose face is `face`, turned to face a camera looking along `rayDirection`. The opaque
// pass culls back faces (an authored mesh carries both windings), so the triangle it draws looks at the camera; when a
// ray meets the triangle's back all the same (the glass and shadow passes cull nothing) the normal is turned toward the
// camera. A normal that still leans away from the ray (an
// interpolated or sampled normal tilted past a grazing ray) is clamped to face the camera, by removing the part of it
// along the ray and a sliver more.
float3 sdfMeshFaceCamera(float3 normal, float3 face, float3 rayDirection) {
    normal = normalize(normal);
    normal = ((dot(normal, face) < 0.0) ? -normal : normal);
    normal = ((dot(face, rayDirection) > 0.0) ? -normal : normal);

    float along = dot(normal, rayDirection);

    return ((along > 0.0) ? normalize(normal - (rayDirection * (along + 1.0e-3))) : normal);
}
// Half the surface divergence of the interpolated unit normal. The barycentric gradients are in world units, so
// a sphere of radius r reports 1/r, a flat plate zero, and a cavity negative curvature. Derive this from the baked
// triangle's normals, before facing them toward the camera: no field query or neighbouring screen pixel is needed.
float sdfMeshMeanCurvature(float3 a, float3 b, float3 c, float3 na, float3 nb, float3 nc, float3 weights) {
    float3 ab = b - a;
    float3 ac = c - a;
    float d00 = dot(ab, ab);
    float d01 = dot(ab, ac);
    float d11 = dot(ac, ac);
    float determinant = d00 * d11 - d01 * d01;
    if (determinant <= max(1.0e-20, d00 * d11 * 1.0e-6)) return 0.0;

    na *= rsqrt(max(dot(na, na), 1.0e-20));
    nb *= rsqrt(max(dot(nb, nb), 1.0e-20));
    nc *= rsqrt(max(dot(nc, nc), 1.0e-20));
    float3 n = weights.x * na + weights.y * nb + weights.z * nc;
    float lengthSquared = dot(n, n);
    if (lengthSquared <= 1.0e-12) return 0.0;

    float inverseLength = rsqrt(lengthSquared);
    n *= inverseLength;
    float3 gradientB = (d11 * ab - d01 * ac) / determinant;
    float3 gradientC = (d00 * ac - d01 * ab) / determinant;
    float3 deltaB = nb - na;
    float3 deltaC = nc - na;
    // Differentiating normalize(n) removes the derivative along n and divides by its original length.
    return 0.5 * inverseLength * (dot(deltaB, gradientB) - dot(n, deltaB) * dot(n, gradientB)
        + dot(deltaC, gradientC) - dot(n, deltaC) * dot(n, gradientC));
}

// The surface normal of a resolved hit's triangle at a world point on it, seen along `rayDirection`: the vertex normals
// interpolated at the point's barycentric coordinates when the mesh carries them, the face normal otherwise, facing the
// camera (sdfMeshFaceCamera). `faceNormal` is the triangle's own unit normal on the camera's side, the plane the
// rasterized surface lies in. Curvature retains the authored outward orientation, independent of the viewing side.
// `frameSlot` is the dynamic-transform slot whose frame the point's material layers shade in (an inset, weathering, a
// paint riding the winning frame) and whose lanes they read: the slot of the shape that won there, as the field's
// winning frame is. A vertex riding a member rides its own shape's slot (SdfMesh.Members), so the point takes the slot
// of the triangle's nearest vertex; any other point takes the draw's frame slot, or -1.
float3 sdfMeshSurfaceNormal(SdfMeshHit hit, float3 surfacePoint, float3 rayDirection, out float3 faceNormal, out float curvature, out int frameSlot) {
    uint record = hit.record;
    uint first = (3u * hit.triangleIndex);
    float3 a, b, c, na, nb, nc;
    sdfMeshWorldVertex(record, first, a, na);
    sdfMeshWorldVertex(record, first + 1u, b, nb);
    sdfMeshWorldVertex(record, first + 2u, c, nc);
    float3 face = cross((b - a), (c - a));
    float3 normal = face;
    float3 weights;
    curvature = 0.0;
    frameSlot = asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD));

    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) != 0u) {
        float3 toA = (surfacePoint - a);
        float3 toB = (surfacePoint - b);
        float3 toC = (surfacePoint - c);
        float nearA = dot(toA, toA);
        float nearB = dot(toB, toB);
        float nearC = dot(toC, toC);
        uint nearest = (((nearA <= nearB) && (nearA <= nearC)) ? first : ((nearB <= nearC) ? (first + 1u) : (first + 2u)));
        uint entry = sdfMeshMemberEntry(record, sdfMeshIndexAt(record, nearest));

        frameSlot = ((entry != 0u) ? (int)sdfMeshMemberSlot(record, entry) : frameSlot);
    }

    if ((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_WORLD_MATERIAL_FLAG) != 0u) {
        frameSlot = SDF_TRANSFORM_SLOT_NONE;
    }
    frameSlot = asint(sdfViewPoseSlot(asuint(frameSlot)));

    faceNormal = (face * rsqrt(max(dot(face, face), 1.0e-30)));
    faceNormal = ((dot(faceNormal, rayDirection) > 0.0) ? -faceNormal : faceNormal);

    if (((sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_NORMALS_FLAG) != 0u) && sdfMeshBarycentric(a, b, c, surfacePoint, weights)) {
        float3 interpolated = weights.x * na + weights.y * nb + weights.z * nc;

        if (dot(interpolated, interpolated) > 1.0e-20) {
            normal = interpolated;
            curvature = sdfMeshMeanCurvature(a, b, c, na, nb, nc, weights);
        }
    }

    return sdfMeshFaceCamera(normal, face, rayDirection);
}

#endif
