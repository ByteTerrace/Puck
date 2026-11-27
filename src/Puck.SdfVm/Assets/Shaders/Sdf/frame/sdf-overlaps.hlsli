// The overlap spans (SdfWorldPackage.OverlapSpans, SdfWorldPackage.WithOverlaps), SDF_OVERLAP_SPAN_WORDS words a render
// pixel, which the overlap raster pass writes (passes/sdf-mesh-overlaps.frag.hlsl) and the overlap debug view reads
// (debug/sdf-debug-views.hlsli):
// 0: the nearest front face behind the pixel's visible surface of a solid that is not the visible surface's object;
// 1: the nearest back face behind the visible surface of any solid.
// Each word holds a ray distance's bits inverted (sdfOverlapKey), so an atomic maximum keeps the nearest face whatever
// order the raster runs in, and the cleared zero reads as none.
//
// The visible surface is where the ray enters its solid. Walking on, the ray either leaves a solid first (the back face)
// or enters another object's solid while still inside the first (the front face): when the front face comes first, the
// ray between the two faces lies inside both solids, the first overlap behind the pixel, and the distance between them is
// that overlap's depth along the ray. A surface of one solid the other passes into is never itself visible (the union's
// boundary is), so the view shows the overlap through the solid that hides it. The test holds for solids whose faces say
// where a ray enters and leaves (closed meshes wound outward, SDF_MESH_ENCLOSED_FLAG); the draws of one object (every
// group, chunk and level of one placement, SdfMeshDraw.ObjectKey) never overlap each other, since their union is the
// object the author made.
#ifndef FRAME_SDF_OVERLAPS_HLSLI
#define FRAME_SDF_OVERLAPS_HLSLI

static const uint SdfOverlapFrontWord = 0u;
static const uint SdfOverlapBackWord = 1u;

uint sdfOverlapSpanWord(uint2 pixel, uint2 extent) {
    return (SDF_OVERLAP_SPAN_WORDS * ((pixel.y * extent.x) + pixel.x));
}
// A ray distance as a span word: its bits inverted, so a nearer distance is a larger word.
uint sdfOverlapKey(float t) {
    return ~asuint(max(t, 0.0));
}
// The ray distance a span word holds, or -1 for none.
float sdfOverlapDistance(uint key) {
    return ((key == 0u) ? -1.0 : asfloat(~key));
}

#if defined(SDF_OVERLAPS_ACCUMULATE)
// The raster writes the spans through the mesh interface's raster accumulation binding, which the glass pass writes its
// sums through (SdfWorldPassRecorder binds the overlap spans there for the overlap pass).
#define SDF_OVERLAP_SPANS_WRITTEN sdfGlassSumsRW

// Whether a draw takes part, with its object key: one that encloses a solid, so its faces say where a ray enters and leaves
// it, and is no body unless the view's options ask for bodies (passGroup.debugOptions.y). A body is the seat's own
// character (the slots its fade names, passGroup.stampFade) or a moving rigid copy (a body and the rows riding it, whose
// object key lacks SDF_MESH_GROUP_OBJECT_KEY_BIT).
bool sdfOverlapTakesPart(uint record, out uint objectKey) {
    uint selection = sdfMeshRecordWord(record + SDF_MESH_SELECTION_WORD + 3u);
    int slot = asint(sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD));
    float3 fade = passGroup.stampFade;
    bool seated = ((fade.y > 0.0) && (slot >= int(fade.x)) && (slot < int(fade.x + fade.y)));

    objectKey = (sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) >> SDF_MESH_OBJECT_KEY_SHIFT);
    bool copy = (((selection & SDF_MESH_DYNAMIC_FLAG) != 0u) && (objectKey != 0u) && ((objectKey & SDF_MESH_GROUP_OBJECT_KEY_BIT) == 0u));

    return (((selection & (SDF_MESH_ENCLOSED_FLAG | SDF_MESH_ABSENT_FLAG)) == SDF_MESH_ENCLOSED_FLAG) &&
        (!(seated || copy) || (passGroup.debugOptions.y > 0.0)));
}
// Takes one face into its pixel's spans. The caller excludes helper lanes, whose pixel lies outside the grid.
void sdfOverlapAccumulate(uint2 pixel, uint draw, uint triangleIndex, float3 world) {
    uint objectKey;
    uint record = sdfMeshRecord(draw);

    if (!sdfOverlapTakesPart(record, objectKey)) {
        return;
    }

    uint visibilityRecord = sdfVisibilityRecord(pixel, 0u, passGroup.imageExtent);
    SdfVisibility visibility = sdfLoadVisibility(visibilityRecord);
    SdfMeshHit hit;
    uint visibleKey;

    // The visible surface's draw comes from GPU data, so it names a record only through the hit door, and a pixel whose
    // visible draw takes no part (a body, an open sheet) has no solid to be inside of.
    if ((sdfVisibilityKind(visibility.identity) != SDF_VISIBILITY_KIND_MESH) ||
        !sdfMeshResolveHit(sdfVisibilitySource(visibility.identity), sdfVisibilityRecordBuffer[visibilityRecord + SdfVisibilityTriangleWord], hit) ||
        !sdfOverlapTakesPart(hit.record, visibleKey)) {
        return;
    }

    ViewportData view = worldView();
    float t = length(world - view.position.xyz);

    // A face at the visible surface is that surface, or one coincident with it.
    if (t <= (visibility.t * (1.0 + 1.0e-5) + 2.0e-4)) {
        return;
    }

    // Facing by the triangle's authored outward winding, since rasterizer facing follows backend viewport conventions.
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    bool front = (dot(cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a)), (world - view.position.xyz)) < 0.0);
    uint word = sdfOverlapSpanWord(pixel, passGroup.imageExtent);
    uint before;

    if (front) {
        if (objectKey != visibleKey) {
            InterlockedMax(SDF_OVERLAP_SPANS_WRITTEN[word + SdfOverlapFrontWord], sdfOverlapKey(t), before);
        }
    } else {
        InterlockedMax(SDF_OVERLAP_SPANS_WRITTEN[word + SdfOverlapBackWord], sdfOverlapKey(t), before);
    }
}
#else
// The depth along the pixel's ray of the first overlap behind its visible surface, in world units, or zero where the ray
// leaves every solid it is inside before it enters another object's, or where the bound spans do not hold the pixel (a
// view whose graph has not yet taken the overlap pass reads the tables' dummy).
float sdfOverlapDepthAt(uint2 pixel, uint2 extent) {
    uint word = sdfOverlapSpanWord(pixel, extent);
    uint count;
    uint stride;

    sdfOverlapSpans.GetDimensions(count, stride);
    if ((word + SdfOverlapBackWord) >= count) {
        return 0.0;
    }

    float front = sdfOverlapDistance(sdfOverlapSpans[word + SdfOverlapFrontWord]);
    float back = sdfOverlapDistance(sdfOverlapSpans[word + SdfOverlapBackWord]);

    return (((front >= 0.0) && (back > front)) ? (back - front) : 0.0);
}
#endif
#endif
