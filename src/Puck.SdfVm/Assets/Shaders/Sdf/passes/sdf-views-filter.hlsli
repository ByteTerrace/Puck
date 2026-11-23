// The views filter (the 4x4 window views averages the surface pass's occlusion and shadow estimates over), the edge
// search (each pixel's distance to the nearest edge of each class, which a shade program reads as its edge.* members),
// and what a binned view keeps of both between its passes: the filter record each pixel's filtered values and edges
// travel in from the filter pass to the shading bins, and the shade bins that sort the view's 8x8 tiles by the kernel
// that shades them. The unsplit views stage gathers, filters and searches in its own group; the binned filter pass does
// so there instead and writes the record, which the binned stage reads in their place. This module holds the one edge
// search: nothing else seeds or measures an edge.
#ifndef PASSES_SDF_VIEWS_FILTER_HLSLI
#define PASSES_SDF_VIEWS_FILTER_HLSLI

// Whether views averages the occlusion, and the shadow visibilities, over the filter's window in a view mode. Both read
// only the pass block and the view's mode, so they are uniform across a dispatch and every pass of one view agrees.
bool sdfViewsFiltersAmbient(int viewMode) {
#if defined(SDF_VIEWS_OMIT_FILTER)
    return false;
#endif
    return (!worldAoDisabled() && (worldFinalShadingMode(viewMode) || (viewMode == SDF_DEBUG_VIEW_AO)));
}
bool sdfViewsFiltersShadows(int viewMode) {
#if defined(SDF_VIEWS_OMIT_FILTER)
    return false;
#endif
    return ((passGroup.shadowSlotCount > 0u) && !worldSoftShadowsDisabled()
        && (worldFinalShadingMode(viewMode) || (viewMode == SDF_DEBUG_VIEW_SHADOW)));
}

// Whether the view searches for edges: a final view of a frame one of whose programs reads an edge.* distance
// (SdfShadeReads.Edges, which the pass block carries as inkEdges). It reads only the pass block and the view's mode, so
// every pass of one view agrees.
bool sdfViewsSearchesEdges(int viewMode) {
#if defined(SDF_VIEWS_OMIT_INK)
    return false;
#endif
    return ((passGroup.inkEdges != 0u) && worldFinalShadingMode(viewMode));
}

// The filter and the search are a shading kernel's: one that declares the shade programs' stamp (isa/sdf-shade.hlsli)
// before this module. The bins pass, which only sorts tiles, declares neither.
#ifdef SDF_SHADE_HLSLI
// A pixel's edges (SdfShadeEdges) as one record word: each distance in render pixels in six bits, SdfViewsEdgeSteps to
// a pixel so the half pixel a seed lies from its nearest pixel is exact, all ones for far, then the crease cosine in
// eight.
static const float SdfViewsEdgeSteps = 14.0;
uint sdfViewsEdgesPack(float4 renderDistance, float creaseCosine) {
    uint4 d = (uint4)round(min(renderDistance, float(SDF_EDGE_REACH)) * SdfViewsEdgeSteps);

    d = uint4(((renderDistance.x > float(SDF_EDGE_REACH)) ? 63u : d.x), ((renderDistance.y > float(SDF_EDGE_REACH)) ? 63u : d.y),
        ((renderDistance.z > float(SDF_EDGE_REACH)) ? 63u : d.z), ((renderDistance.w > float(SDF_EDGE_REACH)) ? 63u : d.w));

    return (d.x | (d.y << 6u) | (d.z << 12u) | (d.w << 18u) | (((uint)round(saturate((creaseCosine * 0.5) + 0.5) * 255.0)) << 24u));
}
// The word of a pixel with no edge within the reach.
static const uint SdfViewsEdgesNone = 0xFFFFFFFFu;
SdfShadeEdges sdfViewsEdgesUnpack(uint word) {
    uint4 d = (uint4(word, (word >> 6u), (word >> 12u), (word >> 18u)) & 63u);
    float scale = (passGroup.displayPixels.x / SdfViewsEdgeSteps);
    SdfShadeEdges edges;

    edges.distance = float4(((d.x == 63u) ? SDF_SHADE_EDGE_FAR : (float(d.x) * scale)), ((d.y == 63u) ? SDF_SHADE_EDGE_FAR : (float(d.y) * scale)),
        ((d.z == 63u) ? SDF_SHADE_EDGE_FAR : (float(d.z) * scale)), ((d.w == 63u) ? SDF_SHADE_EDGE_FAR : (float(d.w) * scale)));
    edges.creaseCosine = (((float(word >> 24u) / 255.0) * 2.0) - 1.0);

    return edges;
}

#endif

#if defined(SDF_SHADE_HLSLI) && !defined(SDF_VIEWS_BINNED)
// The group's tile: its pixels with the edge search's reach of apron on every side (SDF_EDGE_REACH; the pattern
// filter reads two pixels before the group and one after), each as its surface point and the surface pass's occlusion,
// a negative occlusion where it holds no hit, beside its K word, its face, its distance and its keys.
#define SDF_VIEWS_FILTER_TILE_EDGE (SDF_VISIBILITY_GROUP_EDGE + (2u * SDF_EDGE_REACH))
#define SDF_VIEWS_FILTER_TILE_ENTRIES (SDF_VIEWS_FILTER_TILE_EDGE * SDF_VIEWS_FILTER_TILE_EDGE)
// The pattern filter's window starts two pixels before the pixel.
#define SDF_VIEWS_FILTER_WINDOW (SDF_EDGE_REACH - 2u)
groupshared float4 sdfViewsFilterTile[SDF_VIEWS_FILTER_TILE_ENTRIES];
groupshared uint sdfViewsFilterShadows[SDF_VIEWS_FILTER_TILE_ENTRIES];
// An entry's face: its unit normal, decoded once for every reader, and for the edge search the reach of its pixel
// along its own surface, in metres (SdfViewsEdgeDepth). A miss holds zeros.
groupshared float4 sdfViewsFilterFaces[SDF_VIEWS_FILTER_TILE_ENTRIES];
// An entry's distance along its ray, which says which of two surfaces is the nearer.
groupshared float sdfViewsFilterDepths[SDF_VIEWS_FILTER_TILE_ENTRIES];
// An entry's keys, which the edge search compares and nothing else reads: its material, then its draw's object key
// (SDF_MESH_OBJECT_KEY_SHIFT), with SdfViewsEdgeCut set where the entry seeds no edge. Neither names a draw, so two
// chunks of one mesh have nothing to differ by.
groupshared uint2 sdfViewsFilterKeys[SDF_VIEWS_FILTER_TILE_ENTRIES];
// An entry that seeds no edge: one outside the image, and a hit beside its draw's clip plane, so a body cut by a
// portal's glass draws no line along the cut.
static const uint SdfViewsEdgeCut = 0x80000000u;

// The tile's seeds. A seed lies between two neighbouring entries that jump, and each line of the tile (a row, then a
// column) holds one mask a class of the pairs along it that do, bit i for the pair of entries i and i + 1: an occlusion
// the first entry owns, an occlusion the second owns (the nearer surface owns an occlusion), a crease, an object
// boundary, a material boundary, and the union of them all.
#define SDF_VIEWS_EDGE_WORDS 6u
#define SDF_VIEWS_EDGE_MASKS ((2u * SDF_VIEWS_FILTER_TILE_EDGE) * SDF_VIEWS_EDGE_WORDS)
static const uint SdfEdgeDepthFirst = 0u;
static const uint SdfEdgeDepthSecond = 1u;
static const uint SdfEdgeCrease = 2u;
static const uint SdfEdgeObject = 3u;
static const uint SdfEdgeMaterial = 4u;
static const uint SdfEdgeAny = 5u;
groupshared uint sdfViewsEdgeMasks[SDF_VIEWS_EDGE_MASKS];
// Whether the tile holds any seed: a tile with none searches nothing.
groupshared uint sdfViewsEdgeSeeded;

// The pixel footprints one point of a pair lies off the other's tangent plane past which the pair is an occlusion: a
// tangent-plane comparison, so a sloping plane has none. The footprint is the one the other point's pixel covers on
// its own surface, its size across the ray over the cosine the surface is seen at (an entry's reach), since a point a
// pixel from a fold lies that far along its face: a fold one of whose faces is seen at a grazing angle is then a fold
// on every pair along it, never an occlusion on some and a crease on others.
static const float SdfViewsEdgeDepth = 2.25;
// The least cosine a surface's footprint is stretched by.
static const float SdfViewsEdgeGrazing = 0.1;
// The cosine between two neighbours' normals at one depth below which the pair is a crease: thirty degrees, the least
// turn the bake makes a crease of. A program reads the turn itself (edge.creaseCosine) and draws the creases it wants.
static const float SdfViewsEdgeCreaseCosine = 0.866;

// The cosines between a neighbour's normal and the pixel's over which the neighbour's shadow visibility fades into the
// pixel's average: a neighbour turned more than 60 degrees away takes no part, one within 25 degrees a full one.
static const float SdfViewsShadowNormalLow = 0.5;
static const float SdfViewsShadowNormalHigh = 0.9;

// The classes a pair of neighbouring tile entries jumps by, a bit a class. A surface against the sky, and two points
// either of which lies off the other's tangent plane, are occlusions, owned by the surface and by the nearer one; two
// hits at one depth are a crease where their normals turn, an object boundary where their object keys differ and a
// material boundary where their materials do.
uint sdfViewsEdgePair(uint first, uint second) {
    float4 a = sdfViewsFilterTile[first];
    float4 b = sdfViewsFilterTile[second];
    uint2 keyA = sdfViewsFilterKeys[first];
    uint2 keyB = sdfViewsFilterKeys[second];
    bool hitA = (a.w >= 0.0);
    bool hitB = (b.w >= 0.0);

    if ((((keyA.y | keyB.y) & SdfViewsEdgeCut) != 0u) || (!hitA && !hitB)) {
        return 0u;
    }
    if (hitA != hitB) {
        return (1u << (hitA ? SdfEdgeDepthFirst : SdfEdgeDepthSecond));
    }

    float4 faceA = sdfViewsFilterFaces[first];
    float4 faceB = sdfViewsFilterFaces[second];
    float3 delta = (b.xyz - a.xyz);

    // Each point against the other's plane, within the other point's reach along its own face.
    if ((abs(dot(delta, faceA.xyz)) > faceB.w) || (abs(dot(delta, faceB.xyz)) > faceA.w)) {
        return (1u << ((sdfViewsFilterDepths[first] <= sdfViewsFilterDepths[second]) ? SdfEdgeDepthFirst : SdfEdgeDepthSecond));
    }

    return (((dot(faceA.xyz, faceB.xyz) < SdfViewsEdgeCreaseCosine) ? (1u << SdfEdgeCrease) : 0u)
        | ((keyA.y != keyB.y) ? (1u << SdfEdgeObject) : 0u)
        | ((keyA.x != keyB.x) ? (1u << SdfEdgeMaterial) : 0u));
}
// Marks a pair's classes in its line's masks.
void sdfViewsEdgeMark(uint strip, uint pair, uint classes) {
    if (classes == 0u) {
        return;
    }

    uint base = (strip * SDF_VIEWS_EDGE_WORDS);
    uint bit = (1u << pair);

    [unroll]
    for (uint c = 0u; (c < SdfEdgeAny); c++) {
        if ((classes & (1u << c)) != 0u) {
            InterlockedOr(sdfViewsEdgeMasks[base + c], bit);
        }
    }
    InterlockedOr(sdfViewsEdgeMasks[base + SdfEdgeAny], bit);
    InterlockedOr(sdfViewsEdgeSeeded, 1u);
}

// Loads the group's tile from the visibility records: the pattern filter's window alone unless `edges` asks for the
// whole tile, the K words only when `shadows` asks for them, the keys only for `edges`. With `edges` it then seeds the
// tile: every pair of neighbouring entries, once, into the masks of its row or its column. Every lane of the group
// calls it, a lane past the render extent included, since it ends at the group's barriers.
void sdfViewsGatherFilter(SdfPixel p, bool shadows, bool edges) {
    uint2 lane = (p.pixel % SDF_VISIBILITY_GROUP_EDGE);
    uint first = ((lane.y * SDF_VISIBILITY_GROUP_EDGE) + lane.x);
    int2 origin = (int2(p.pixel - lane) - (int)SDF_EDGE_REACH);
    int2 dims = int2(worldViewDims(p.view));
    uint index;

    for (index = first; (index < SDF_VIEWS_FILTER_TILE_ENTRIES); index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
        uint2 at = uint2((index % SDF_VIEWS_FILTER_TILE_EDGE), (index / SDF_VIEWS_FILTER_TILE_EDGE));
        int2 pixel = (origin + int2(at));
        float4 entry = float4(0.0, 0.0, 0.0, -1.0);
        uint word = 0u;
        float4 face = float4(0.0, 0.0, 0.0, 0.0);
        float depth = 0.0;
        uint2 keys = uint2(0u, SdfViewsEdgeCut);
        bool windowed = (all(at >= SDF_VIEWS_FILTER_WINDOW) && all(at < ((SDF_VIEWS_FILTER_WINDOW + SDF_VISIBILITY_GROUP_EDGE) + 3u)));

        if ((edges || windowed) && all(pixel >= 0) && all(pixel < dims)) {
            uint record = worldVisibilityRecord(uint2(pixel), p.viewIndex);
            float t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);

            keys.y = 0u;
            // A miss records the far distance.
            if (t < p.farDistance) {
                float3 direction = cameraRayDirection(p.view, ((float2(pixel) + 0.5) / float2(dims)));
                entry = float4((p.rayOrigin + (direction * t)), f16tof32(sdfVisibilityRecordBuffer[record + SdfVisibilityShadingWord] >> 16u));
                word = (shadows ? sdfVisibilityRecordBuffer[record + SdfVisibilityShadowWord] : 0u);
                depth = t;
                if (shadows || edges) {
                    face.xyz = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
                }
                if (edges) {
                    face.w = (((SdfViewsEdgeDepth * p.pixelFootprint) * t) / max(abs(dot(face.xyz, direction)), SdfViewsEdgeGrazing));

                    uint draw = sdfVisibilitySource(sdfVisibilityRecordBuffer[record + SdfVisibilityIdentityWord]);

                    keys.x = sdfVisibilityRecordBuffer[record + SdfVisibilityMaterialWord];
                    if (draw < passGroup.meshDraws) {
                        uint meshRecord = sdfMeshRecord(draw);
                        uint flags = sdfMeshRecordWord(meshRecord + SDF_MESH_FLAGS_WORD);

                        keys.y = (flags >> SDF_MESH_OBJECT_KEY_SHIFT);
                        if (((flags & SDF_MESH_CLIP_FLAG) != 0u) && (sdfMeshClipDistance(meshRecord, entry.xyz) < ((3.0 * p.pixelFootprint) * t))) {
                            keys.y |= SdfViewsEdgeCut;
                        }
                    }
                }
            }
        }

        sdfViewsFilterTile[index] = entry;
        sdfViewsFilterShadows[index] = word;
        sdfViewsFilterFaces[index] = face;
        sdfViewsFilterDepths[index] = depth;
        sdfViewsFilterKeys[index] = keys;
    }
    if (edges) {
        for (index = first; (index < SDF_VIEWS_EDGE_MASKS); index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
            sdfViewsEdgeMasks[index] = 0u;
        }
        if (first == 0u) {
            sdfViewsEdgeSeeded = 0u;
        }
    }

    GroupMemoryBarrierWithGroupSync();

    if (edges) {
        for (index = first; (index < SDF_VIEWS_FILTER_TILE_ENTRIES); index += (SDF_VISIBILITY_GROUP_EDGE * SDF_VISIBILITY_GROUP_EDGE)) {
            uint2 at = uint2((index % SDF_VIEWS_FILTER_TILE_EDGE), (index / SDF_VIEWS_FILTER_TILE_EDGE));

            if (at.x < (SDF_VIEWS_FILTER_TILE_EDGE - 1u)) {
                sdfViewsEdgeMark(at.y, at.x, sdfViewsEdgePair(index, (index + 1u)));
            }
            if (at.y < (SDF_VIEWS_FILTER_TILE_EDGE - 1u)) {
                sdfViewsEdgeMark((SDF_VIEWS_FILTER_TILE_EDGE + at.x), at.y, sdfViewsEdgePair(index, (index + SDF_VIEWS_FILTER_TILE_EDGE)));
            }
        }

        GroupMemoryBarrierWithGroupSync();
    }
}

// One line of the tile from position `at`, ahead or behind: for each class (occlusion, crease, object, material) the
// steps to the nearest pair that stops its search, SDF_EDGE_REACH where none is nearer, and whether that pair is
// a seed of the class this side of it draws. A search never crosses an occlusion or an object boundary, so a pixel
// measures to edges on its own surface alone, and each side of a boundary draws its own half of the line.
void sdfViewsEdgeLine(uint strip, uint at, bool ahead, out uint4 steps, out bool4 seeded, out uint4 pairs) {
    uint base = (strip * SDF_VIEWS_EDGE_WORDS);
    uint depthFirst = sdfViewsEdgeMasks[base + SdfEdgeDepthFirst];
    uint depthSecond = sdfViewsEdgeMasks[base + SdfEdgeDepthSecond];
    uint crease = sdfViewsEdgeMasks[base + SdfEdgeCrease];
    uint object = sdfViewsEdgeMasks[base + SdfEdgeObject];
    uint material = sdfViewsEdgeMasks[base + SdfEdgeMaterial];
    uint surface = ((depthFirst | depthSecond) | object);
    uint4 stops = uint4((depthFirst | depthSecond), (surface | crease), surface, (surface | material));
    // Ahead the pixel is on the pair's first side, behind on its second.
    uint4 seeds = uint4((ahead ? depthFirst : depthSecond), crease, object, material);

    [unroll]
    for (uint c = 0u; (c < 4u); c++) {
        uint mask = (ahead ? (stops[c] >> at) : (stops[c] & ((1u << at) - 1u)));
        uint step = ((mask == 0u) ? SDF_EDGE_REACH : min((ahead ? (uint)firstbitlow(mask) : ((at - 1u) - (uint)firstbithigh(mask))), SDF_EDGE_REACH));
        uint pair = ((ahead ? (at + step) : ((at - 1u) - step)) & 31u);

        steps[c] = step;
        pairs[c] = pair;
        seeded[c] = ((step < SDF_EDGE_REACH) && (((seeds[c] >> pair) & 1u) != 0u));
    }
}
// The pixel's edges from the tile's seeds, as their record word: each class's nearest seed the pixel reaches down its
// own column and then along a row, at the pair's midpoint, in render pixels; a tile with no seed searches nothing.
// Every bound is the reach and none is the tile's, so a pixel reads the same edges whichever tile holds it.
uint sdfViewsEdgesWord(SdfPixel p) {
    if (sdfViewsEdgeSeeded == 0u) {
        return SdfViewsEdgesNone;
    }

    uint2 at = ((p.pixel % SDF_VISIBILITY_GROUP_EDGE) + SDF_EDGE_REACH);
    uint entry = ((at.y * SDF_VIEWS_FILTER_TILE_EDGE) + at.x);
    float reach = float(SDF_EDGE_REACH * SDF_EDGE_REACH);
    float4 nearest = float4(1.0e6, 1.0e6, 1.0e6, 1.0e6);
    // The crease's two entries.
    uint2 creased = uint2(entry, entry);
    uint4 up;
    uint4 down;
    uint4 steps;
    uint4 pairs;
    bool4 seeded;
    uint c;

    sdfViewsEdgeLine((SDF_VIEWS_FILTER_TILE_EDGE + at.x), at.y, true, up, seeded, pairs);
    [unroll]
    for (c = 0u; (c < 4u); c++) {
        float d = ((float(up[c]) + 0.5) * (float(up[c]) + 0.5));

        if (seeded[c] && (d < nearest[c])) {
            nearest[c] = d;
            if (c == 1u) { creased = uint2(((pairs[c] * SDF_VIEWS_FILTER_TILE_EDGE) + at.x), (((pairs[c] + 1u) * SDF_VIEWS_FILTER_TILE_EDGE) + at.x)); }
        }
    }
    sdfViewsEdgeLine((SDF_VIEWS_FILTER_TILE_EDGE + at.x), at.y, false, down, seeded, pairs);
    [unroll]
    for (c = 0u; (c < 4u); c++) {
        float d = ((float(down[c]) + 0.5) * (float(down[c]) + 0.5));

        if (seeded[c] && (d < nearest[c])) {
            nearest[c] = d;
            if (c == 1u) { creased = uint2(((pairs[c] * SDF_VIEWS_FILTER_TILE_EDGE) + at.x), (((pairs[c] + 1u) * SDF_VIEWS_FILTER_TILE_EDGE) + at.x)); }
        }
    }

    [loop]
    for (int offset = (1 - (int)SDF_EDGE_REACH); (offset < (int)SDF_EDGE_REACH); offset++) {
        uint row = (uint)((int)at.y + offset);
        // The rows a class reaches: up to its stop in the column, the stop's near side included.
        uint rows = (uint)abs(offset);
        bool4 reached = ((offset >= 0) ? (uint4(rows, rows, rows, rows) <= up) : (uint4(rows, rows, rows, rows) <= down));

        if ((sdfViewsEdgeMasks[(row * SDF_VIEWS_EDGE_WORDS) + SdfEdgeAny] == 0u) || !any(reached)) {
            continue;
        }

        [unroll]
        for (uint side = 0u; (side < 2u); side++) {
            sdfViewsEdgeLine(row, at.x, (side == 0u), steps, seeded, pairs);
            [unroll]
            for (c = 0u; (c < 4u); c++) {
                float d = (((float(steps[c]) + 0.5) * (float(steps[c]) + 0.5)) + float(offset * offset));

                if (reached[c] && seeded[c] && (d < nearest[c])) {
                    nearest[c] = d;
                    if (c == 1u) { creased = uint2(((row * SDF_VIEWS_FILTER_TILE_EDGE) + pairs[c]), (((row * SDF_VIEWS_FILTER_TILE_EDGE) + pairs[c]) + 1u)); }
                }
            }
        }
    }

    return sdfViewsEdgesPack(sqrt(nearest), ((nearest.y <= reach)
        ? dot(sdfViewsFilterFaces[creased.x].xyz, sdfViewsFilterFaces[creased.y].xyz)
        : 1.0));
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
    uint2 lane = ((p.pixel % SDF_VISIBILITY_GROUP_EDGE) + SDF_VIEWS_FILTER_WINDOW);
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

            float turn = dot(sdfViewsFilterFaces[index].xyz, normal);
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
// bits 20 to 31 of the second. The third word is the pixel's edges (sdfViewsEdgesPack). The record holds only a hit's
// values, and only those its view's mode filters or searches.
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
// each; then each tile's bin; then the tile list, every bin's tiles together in bin order; then each tile's light masks,
// SDF_SHADE_TILE_LIGHT_WORDS words a tile (sdf-views-lights.hlsli). Bin zero holds the tiles no pixel of which has a
// program, which the plain kernel shades; bin one every other tile, which the frame's shading selection shades.
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
    return (viewIndex * (SDF_SHADE_TILE_HEADER_WORDS + ((2u + SDF_SHADE_TILE_LIGHT_WORDS) * sdfShadeTileCount())));
}
uint sdfShadeTileBinWord(uint viewIndex, uint tile) {
    return ((sdfShadeBinsBase(viewIndex) + SDF_SHADE_TILE_HEADER_WORDS) + tile);
}
uint sdfShadeListWord(uint viewIndex, uint entry) {
    return (((sdfShadeBinsBase(viewIndex) + SDF_SHADE_TILE_HEADER_WORDS) + sdfShadeTileCount()) + entry);
}
// The first word of a tile's light masks: the tile's light mask words, then its screen mask.
uint sdfShadeTileLightsWord(uint viewIndex, uint tile) {
    return (((sdfShadeBinsBase(viewIndex) + SDF_SHADE_TILE_HEADER_WORDS) + (2u * sdfShadeTileCount())) + (SDF_SHADE_TILE_LIGHT_WORDS * tile));
}
// The tile a pixel lies in, in the row order the bins count tiles in.
uint sdfShadeTileOf(uint2 pixel) {
    return (((pixel.y / SDF_VISIBILITY_GROUP_EDGE) * sdfShadeTilesAcross()) + (pixel.x / SDF_VISIBILITY_GROUP_EDGE));
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
