// The views filter (the 4x4 window views averages the surface pass's occlusion and shadow estimates over) and what a
// binned view keeps of it between its passes: the filter record each pixel's filtered values travel in from the filter
// pass to the shading bins, and the shade bins that sort the view's 8x8 tiles by the kernel that shades them. The
// unsplit views stage gathers and filters in its own group; the binned filter pass gathers and filters there instead and
// writes the record, which the binned stage reads in the filter's place.
#ifndef PASSES_SDF_VIEWS_FILTER_HLSLI
#define PASSES_SDF_VIEWS_FILTER_HLSLI

// Whether views averages the occlusion, and the shadow visibilities, over the filter's window in a view mode. Both read
// only the pass block and the view's mode, so they are uniform across a dispatch and every pass of one view agrees.
bool sdfViewsFiltersAmbient(int viewMode) {
    return (!worldAoDisabled() && (worldFinalShadingMode(viewMode) || (viewMode == SDF_DEBUG_VIEW_AO)));
}
bool sdfViewsFiltersShadows(int viewMode) {
    return ((passGroup.shadowSlotCount > 0u) && !worldSoftShadowsDisabled()
        && (worldFinalShadingMode(viewMode) || (viewMode == SDF_DEBUG_VIEW_SHADOW)));
}

#ifndef SDF_VIEWS_BINNED
// The pattern filter's tile: the group's pixels with two pixels of apron before them and one after on each axis, each as
// its surface point and the surface pass's occlusion, a negative occlusion where it holds no hit, beside its K word and
// its packed normal.
#define SDF_VIEWS_FILTER_TILE_EDGE (SDF_VISIBILITY_GROUP_EDGE + 3u)
groupshared float4 sdfViewsFilterTile[SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE];
groupshared uint sdfViewsFilterShadows[SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE];
groupshared uint sdfViewsFilterNormals[SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE];

// The cosines between a neighbour's normal and the pixel's over which the neighbour's shadow visibility fades into the
// pixel's average: a neighbour turned more than 60 degrees away takes no part, one within 25 degrees a full one.
static const float SdfViewsShadowNormalLow = 0.5;
static const float SdfViewsShadowNormalHigh = 0.9;

// Loads the group's filter tile from the visibility records, the K words only when `shadows` asks for them. Every lane of
// the group calls it, a lane past the render extent included, since it ends at the group's barrier.
void sdfViewsGatherFilter(SdfPixel p, bool shadows) {
    uint2 lane = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    int2 origin = (int2(p.pixel - lane) - 2);
    int2 dims = int2(worldViewDims(p.view));

    for (uint index = ((lane.y * SDF_VISIBILITY_GROUP_EDGE) + lane.x); (index < (SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE));
        index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
        int2 pixel = (origin + int2((index % SDF_VIEWS_FILTER_TILE_EDGE), (index / SDF_VIEWS_FILTER_TILE_EDGE)));
        float4 entry = float4(0.0, 0.0, 0.0, -1.0);
        uint word = 0u;
        uint normal = SDF_VISIBILITY_ZERO_NORMAL;

        if (all(pixel >= 0) && all(pixel < dims)) {
            uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
            float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);

            // A miss records the far distance.
            if (t < p.farDistance) {
                float3 direction = cameraRayDirection(p.view, ((float2(pixel) + 0.5) / float2(dims)));
                entry = float4((p.rayOrigin + (direction * t)), f16tof32(sdfVisibilityRecordBuffer[record + SdfVisibilityShadingWord] >> 16u));
                word = (shadows ? sdfVisibilityRecordBuffer[record + SdfVisibilityShadowWord] : 0u);
                normal = (shadows ? sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord] : SDF_VISIBILITY_ZERO_NORMAL);
            }
        }

        sdfViewsFilterTile[index] = entry;
        sdfViewsFilterShadows[index] = word;
        sdfViewsFilterNormals[index] = normal;
    }

    GroupMemoryBarrierWithGroupSync();
}
// The pixel's occlusion and shadow visibilities averaged over the 4x4 window at offsets -2 to 1 around it, which holds
// each of the surface pass's sixteen tap patterns once (sdfMeshPattern in sdf-surface-stage.hlsli). A pixel joins the
// average only while its surface point lies within two pixel footprints of this pixel's tangent plane, so neither
// crosses a depth edge or a silhouette; the pixel itself always joins. A slot's shadow average takes only the pixels
// whose slot the surface pass queried: a side turned from a light holds no measurement, and its decoded full visibility
// would light the band beside it on the side that faces the light (on a curved surface, along the whole terminator),
// with the light's whole illuminance under a cosine just above zero wherever the cascades shadow it. A pixel joins the
// shadows' average only while its normal also turns little from this pixel's, so a crease's two lit faces keep their
// own shadows. A slot no pixel of the window queried reads fully visible.
void sdfViewsFilter(SdfPixel p, float3 surfacePoint, float3 normal, float t, out float ambient, out float4 shadows) {
    uint2 lane = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    float tolerance = (2.0 * (p.pixelFootprint * t));
    float ambientSum = 0.0;
    float4 shadowSum = float4(0.0, 0.0, 0.0, 0.0);
    float weight = 0.0;
    float4 shadowWeight = float4(0.0, 0.0, 0.0, 0.0);

    [unroll]
    for (uint y = 0u; (y < 4u); y++) {
        [unroll]
        for (uint x = 0u; (x < 4u); x++) {
            uint index = ((((lane.y + y) * SDF_VIEWS_FILTER_TILE_EDGE) + lane.x) + x);
            float4 entry = sdfViewsFilterTile[index];
            float w = ((entry.w < 0.0) ? 0.0 : saturate(1.0 - (abs(dot((entry.xyz - surfacePoint), normal)) / tolerance)));

            float turn = dot(sdfVisibilityUnpackNormal(sdfViewsFilterNormals[index]), normal);
            uint word = sdfViewsFilterShadows[index];
            float4 ws = ((w * saturate((turn - SdfViewsShadowNormalLow) / (SdfViewsShadowNormalHigh - SdfViewsShadowNormalLow))) * sdfShadowQueried(word));

            ambientSum += (w * entry.w);
            shadowSum += (ws * sdfUnpackShadowVisibility(word));
            weight += w;
            shadowWeight += ws;
        }
    }

    ambient = (ambientSum / max(weight, 1.0e-4));
    shadows = float4(
        ((shadowWeight.x > 0.0) ? (shadowSum.x / shadowWeight.x) : 1.0),
        ((shadowWeight.y > 0.0) ? (shadowSum.y / shadowWeight.y) : 1.0),
        ((shadowWeight.z > 0.0) ? (shadowSum.z / shadowWeight.z) : 1.0),
        ((shadowWeight.w > 0.0) ? (shadowSum.w / shadowWeight.w) : 1.0));
}
#endif

// A pixel's filter record: SDF_VIEWS_FILTER_WORDS words, addressed like its visibility record. The filtered occlusion is
// UNORM16 in bits 0 to 15 of the first word; the four shadow visibilities are UNORM12, x in bits 16 to 27 of the first
// word, y in bits 28 to 31 of the first and 0 to 7 of the second (its low four bits first), z in bits 8 to 19 and w in
// bits 20 to 31 of the second. The record holds only a hit's values, and only those its view's mode filters.
uint sdfViewsFilterRecord(uint2 pixel, uint viewIndex) {
    return (SDF_VIEWS_FILTER_WORDS * ((((viewIndex * passGroup.imageExtent.y) + pixel.y) * passGroup.imageExtent.x) + pixel.x));
}
uint2 sdfViewsFilterPack(float ambient, float4 shadows) {
    uint a = (uint)round(saturate(ambient) * 65535.0);
    uint4 s = (uint4)round(saturate(shadows) * 4095.0);

    return uint2((a | (s.x << 16u) | (s.y << 28u)), ((s.y >> 4u) | (s.z << 8u) | (s.w << 20u)));
}
void sdfViewsFilterUnpack(uint2 words, out float ambient, out float4 shadows) {
    uint4 s = uint4(((words.x >> 16u) & 0xFFFu), (((words.x >> 28u) | (words.y << 4u)) & 0xFFFu), ((words.y >> 8u) & 0xFFFu), (words.y >> 20u));

    ambient = (float(words.x & 0xFFFFu) / 65535.0);
    shadows = (float4(s) / 4095.0);
}

// A view's shade bins (SdfWorldPackage.ShadeTiles), over its 8x8 tiles in row order from the render extent's corner: a
// header of each bin's tile count, the bin's first entry in the tile list and the scatter's cursor, SDF_SHADE_BINS words
// each; then each tile's bin; then the tile list, every bin's tiles together in bin order. Bin zero holds the tiles no
// pixel of which has a program, which the plain kernel shades; bin one every other tile, which the frame's shading
// selection shades.
static const uint SdfShadeCountWord = 0u;
static const uint SdfShadeOffsetWord = SDF_SHADE_BINS;
static const uint SdfShadeCursorWord = (2u * SDF_SHADE_BINS);
static const uint SdfShadeBinPlain = 0u;
static const uint SdfShadeBinShaded = 1u;

uint sdfShadeTilesAcross() {
    return ((passGroup.imageExtent.x + (SDF_VISIBILITY_GROUP_EDGE - 1u)) / SDF_VISIBILITY_GROUP_EDGE);
}
uint sdfShadeTileCount() {
    return (sdfShadeTilesAcross() * ((passGroup.imageExtent.y + (SDF_VISIBILITY_GROUP_EDGE - 1u)) / SDF_VISIBILITY_GROUP_EDGE));
}
uint sdfShadeBinsBase(uint viewIndex) {
    return (viewIndex * (SDF_SHADE_TILE_HEADER_WORDS + (2u * sdfShadeTileCount())));
}
uint sdfShadeTileBinWord(uint viewIndex, uint tile) {
    return ((sdfShadeBinsBase(viewIndex) + SDF_SHADE_TILE_HEADER_WORDS) + tile);
}
uint sdfShadeListWord(uint viewIndex, uint entry) {
    return (((sdfShadeBinsBase(viewIndex) + SDF_SHADE_TILE_HEADER_WORDS) + sdfShadeTileCount()) + entry);
}
// A bin's job width in tiles (SdfShadeDispatch), zero for a bin the frame records no job of.
uint sdfShadeBinWidth(uint bin) {
    return sdfComponent(passGroup.shadeBinWidths, bin);
}
// The first word of a bin's job's indirect arguments (SdfWorldPackage.ShadeArgumentsWritten): three group counts, a
// record for every tile of the view a bin, since a bin's jobs are at least one tile wide.
uint sdfShadeArgumentWord(uint viewIndex, uint bin, uint job) {
    return (3u * ((((viewIndex * SDF_SHADE_BINS) + bin) * sdfShadeTileCount()) + job));
}

#endif
