// The reflection records (SdfWorldPackage.ViewsReflections): what a mirror ray reaches on screen, written by the reflection
// pass (passes/sdf-world-views-reflections.comp.hlsl) and blended by views over the irradiance volume's reflection
// (sdfViewsScreenReflection). The pass traces one ray a cell of passGroup.screenReflections pixels a side, from one pixel
// of the cell, its representative (sdfViewsReflectionPhase), and a record is one cell's, a uint4 indexed by cell
// (sdfViewsReflectionCell):
// x: the colour the ray reached, red and green as halves, at the stored light scale (sdfStoredLightScale).
// y: its blue as a half in the low bits; above it, as UNORM8, the share of the reflection the colour answers, then the
//    share the open sky answers. Shares summing to zero leave the volume's reflection whole.
// z: the representative's geometric normal as the visibility record packs it (sdfVisibilityPackNormal), or
//    SDF_VISIBILITY_ZERO_NORMAL where the cell traced no ray, whose record no pixel reads.
// w: the representative's tangent plane: its surface point's distance along that normal from the view's eye, as float
//    bits, against which a pixel tests whether it lies on the representative's surface.
#ifndef PASSES_SDF_VIEWS_REFLECTIONS_HLSLI
#define PASSES_SDF_VIEWS_REFLECTIONS_HLSLI

#if SDF_VIEWS_REFLECTION_WORDS != 4
#error The reflection record is one uint4 a cell (SdfWorldPackage.ViewsReflectionWords).
#endif

// The cells a view's records cover along each axis at a cell edge: the view's 8x8 tiles, as SdfWorldPackage counts the
// records per tile of the render extent.
uint2 sdfViewsReflectionCells(uint cellEdge) {
    uint2 tiles = ((passGroup.imageExtent + (SDF_VISIBILITY_GROUP_EDGE - 1u)) / SDF_VISIBILITY_GROUP_EDGE);

    return (tiles * (SDF_VISIBILITY_GROUP_EDGE / cellEdge));
}
// A cell's record: its place among every view's cells, raster order within a view.
uint sdfViewsReflectionCell(uint2 cell, uint viewIndex, uint cellEdge) {
    uint2 cells = sdfViewsReflectionCells(cellEdge);

    return ((((viewIndex * cells.y) + cell.y) * cells.x) + cell.x);
}
// The pixel of each cell that traces this frame, as its offset within the cell: the four pixels of a 2x2 cell in turn,
// diagonals first, so the frames a still view averages read every pixel's own ray; a cell of one pixel traces it.
uint2 sdfViewsReflectionPhase(uint historyFrames, uint cellEdge) {
    uint turn = (historyFrames & 3u);

    return ((cellEdge > 1u) ? uint2((turn & 1u), ((turn >> 1u) ^ (turn & 1u))) : uint2(0u, 0u));
}

uint4 sdfViewsReflectionPack(float3 color, float hit, float open, uint normalWord, float plane) {
    uint2 hitOpen = (uint2)round(saturate(float2(hit, open)) * 255.0);

    return uint4((f32tof16(color.r) | (f32tof16(color.g) << 16u)), (f32tof16(color.b) | (hitOpen.x << 16u) | (hitOpen.y << 24u)), normalWord, asuint(plane));
}

#if !defined(SDF_VIEWS_REFLECTIONS_PASS)
// What the reflection records answer at a pixel: the shares of the reflection the colour the rays reached (`hit`) and the
// open sky (`open`) answer, and the colour weighed by its share. `kept` is false where no record answers (a view whose
// fragment holds no reflection pass, or no cell on the pixel's surface), and the volume's reflection stands whole.
struct SdfViewsScreenRead {
    bool kept;
    float hit;
    float open;
    float3 color;
};

// A views pixel's read of the reflection records. A pixel weighs the 2x2 cells about it by a tent of one cell's width about
// each cell's representative this frame, so along each axis its own representative counts whole and two a pixel either
// side count alike, and by how nearly each representative lies on its own surface: on its tangent plane and facing as it
// does, falling smoothly to nothing at the probe lookup's tolerances (sdfViewsProbeTolerance, SdfViewsProbeFacing). A
// reflection so never crosses a depth or crease edge the records show, and a cell's ray never switches whole between
// counting and not. `surfacePoint` is the pixel's, `normal` its geometric normal (the visibility record's) and `t` its
// ray distance.
SdfViewsScreenRead sdfViewsScreenRead(SdfPixel p, float3 surfacePoint, float3 normal, float t) {
    SdfViewsScreenRead none;

    none.kept = false;
    none.hit = 0.0;
    none.open = 0.0;
    none.color = float3(0.0, 0.0, 0.0);

    uint cellEdge = passGroup.screenReflections;

    if (cellEdge == 0u) {
        return none;
    }

    int2 phase = int2(sdfViewsReflectionPhase(passGroup.historyFrames, cellEdge));
    // The cell whose representative lies at or before the pixel along each axis: the shift floors a negative offset.
    int2 base = ((cellEdge > 1u) ? ((int2(p.pixel) - phase) >> 1) : int2(p.pixel));
    int2 cells = int2(sdfViewsReflectionCells(cellEdge));
    float3 relative = (surfacePoint - passGroup.viewPosition);
    float footprint = (p.pixelFootprint * t);
    float planeTolerance = sdfViewsProbeTolerance(footprint, abs(dot(normal, p.rayDirection))).y;
    float scale = sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale);
    float edge = float(cellEdge);
    int2 last = (int2(passGroup.imageExtent) - 1);
    float kept = 0.0;
    float hit = 0.0;
    float open = 0.0;
    float3 color = float3(0.0, 0.0, 0.0);

    [unroll]
    for (uint corner = 0u; (corner < 4u); corner++) {
        int2 cell = (base + int2((int)(corner & 1u), (int)(corner >> 1u)));

        if (any(cell < 0) || any(cell >= cells)) {
            continue;
        }

        // The cell's representative this frame, clamped into the extent as the reflection pass clamps it.
        float2 tent = saturate(1.0 - (abs(float2(min(((cell * (int)cellEdge) + phase), last) - int2(p.pixel))) / edge));
        float weight = (tent.x * tent.y);

        if (weight <= 0.0) {
            continue;
        }

        uint4 record = sdfViewsReflections[sdfViewsReflectionCell(uint2(cell), p.viewIndex, cellEdge)];

        if (record.z == SDF_VISIBILITY_ZERO_NORMAL) {
            continue;
        }

        float3 representative = sdfVisibilityUnpackNormal(record.z);
        float gap = abs(dot(relative, representative) - asfloat(record.w));

        // Whole within half of each tolerance, nothing past it.
        weight *= (saturate(2.0 * (1.0 - (gap / planeTolerance))) *
            saturate((dot(normal, representative) - SdfViewsProbeFacing) / (0.5 * (1.0 - SdfViewsProbeFacing))));

        float cellHit = (weight * (float((record.y >> 16u) & 0xFFu) / 255.0));

        kept += weight;
        hit += cellHit;
        open += (weight * (float(record.y >> 24u) / 255.0));
        color += (cellHit * float3(f16tof32(record.x), f16tof32(record.x >> 16u), f16tof32(record.y)));
    }
    if (kept <= 0.0) {
        return none;
    }

    // The shares the kept cells answer, easing to the volume's alone as the weight kept falls below half the tent's.
    float answered = (saturate(2.0 * kept) / kept);
    SdfViewsScreenRead read;

    read.kept = true;
    read.hit = (hit * answered);
    read.open = (open * answered);
    read.color = (color * (answered / scale));
    return read;
}
// Whether the reflection takes any of the sky: its open share, or the volume's share of it where the records leave the
// volume a share. An exactly zero weight means the sky is not read at all, so the caller evaluates it only when this is
// true.
bool sdfViewsReflectionReadsSky(SdfViewsScreenRead read, SdfReflectionShare share) {
    if (!read.kept) {
        return (share.sky != 0.0);
    }
    return ((read.open != 0.0) || ((share.sky != 0.0) && (saturate(1.0 - (read.hit + read.open)) != 0.0)));
}
// The environment a views pixel reflects: the volume's reflection (`share`, sdfViewsReflectionShare) where no ray answers
// it, the colour the rays reached on screen and the open sky (`sky`, worldEnvironmentReflection) by the shares the records
// hold. `sky` may be zero when sdfViewsReflectionReadsSky is false.
float3 sdfViewsScreenReflection(SdfViewsScreenRead read, SdfReflectionShare share, float3 sky) {
    float3 fallback = ((sky * share.sky) + share.added);

    if (!read.kept) {
        return fallback;
    }
    return (((fallback * saturate(1.0 - (read.hit + read.open))) + read.color) + (sky * read.open));
}
#endif

#endif
