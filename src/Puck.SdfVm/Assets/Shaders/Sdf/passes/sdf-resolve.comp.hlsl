// The view's one path from its render extent to its output extent (SdfWorldPackage.Resolve), one invocation an output
// pixel. It resolves the lit image, color premultiplied by coverage and fog transmittance with coverage in alpha, which
// views wrote for every render sample.
// Beside it, it resolves each sample's surface transport (shade/sdf-transport.hlsli), computed from the sample's coverage
// and its surface's air along its own jittered ray, the ray its color's transmittance was taken along, with exactly the color's weights, so the
// composite's atmosphere and media see the coverage the color has. Spatial, it reconstructs the active render grid without sampling unrendered ceiling pixels
// (reconstruction.hlsli), reading both quantities' taps over one footprint. Temporal (passGroup.temporal, with no debug
// view), the history holds, per output pixel, the weighted mean of every jittered sample the pixel has gathered,
// coverage with color and transport, and their summed weight:
// - this frame's samples are the 3x3 render samples nearest the output pixel's center, each weighted by a Gaussian of
//   its distance in output pixels;
// - the pixel's motion is the nearest-depth sample's, reprojected through sdfReprojection's modules, or, where the 3x3
//   saw no surface, the camera's rotation alone;
// - the history at the moved position is rejected when the receiver the history surface there names (the tap nearest
//   that pixel's center that is not a sub-pixel feature) matches no 3x3 tap's identity within SdfHistoryDepthTolerance
//   of its ray distance; the pixel then shows the spatial path at this frame's sample grid and its history restarts from
//   this frame's samples;
// - surviving history is clipped to the 3x3's YCoCg, coverage and transport box, its weight lowered by the reactivity,
//   and joined by this frame's samples; until a full sample's weight is gathered the spatial path shows through, and a
//   reactive pixel shows it by its reactivity;
// - a stationary, nonreactive receiver with a complete phase window instead shows that window's fixed-order mean,
//   replacing each phase within a period and retaining the samples this phase's neighborhood misses;
// - a sub-pixel feature (sdfResolveFeature) the footprint showed within the last jitter period widens the box to its
//   color while the surface stands still, so the mean the jitter gathers over the feature survives the frames that miss
//   it, as a supersampled image holds it, and the history saturates at SDF_HISTORY_FEATURE_WEIGHT_CAP rather than one
//   period, so the mean barely pulses with the jitter.
// The first frame of an epoch reads no history and writes the spatial path's color and transport word, computed at the
// one site below that both paths run, so it is the spatial path's frame exactly. Where the output has the render grid's
// extent and no jitter, each pixel is its render sample copied whole, and its transport word carries that sample's ray
// distance, which the composite turns into transport with a native view's own arithmetic. The history surface holds the
// gathered weight, capped at one jitter period of full-weight samples. Beside the lit image it writes each output
// pixel's transport words (sdf-transport.hlsli), which the composite scales each atmosphere kind's in-scatter by and clips
// the volumes by. The sky never enters the history: the sky and composite passes follow this one.
#define SDF_DYNAMIC_TRANSFORMS
#include "../isa/sdf-resolve.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-debug-modes.hlsli"
#include "../frame/sdf-reprojection.hlsli"
#include "../frame/sdf-history-surface.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "Shared/reconstruction.hlsli"
#include "Shared/shared-exponent.hlsli"

// The history surface holds SDF_HISTORY_SURFACE_WORDS an output pixel (SdfWorldPackage.HistorySurfaceWords), in planes:
// word w of every output pixel lies in plane w, extent.x * extent.y words long (sdfHistoryWord), so a wave's lanes read
// or write one word of adjacent pixels in one coalesced transaction; with a pixel's words adjacent instead, every lane
// of every access touches its own sector. The words are: the identity of the receiver, then its tangent-plane distance along the output ray and gathered weight as two half floats (the
// distance low; the far limit 8192 and the weight cap stay normal halves, each within a relative 2^-11, far inside
// SdfHistoryDepthTolerance), then the accumulated transport's two words (sdfPackTransport), then the remembered feature:
// its YCoCg as three half floats, Y low and Co high in the fifth word, Cg low in the sixth with the frames the memory has
// left above it, zero for none, and two high bits for the directions eligible for shared shading. The weight saturates at SDF_HISTORY_WEIGHT_CAP, one jitter period of full-weight samples
// (SdfWorldPackage.TemporalPeriod), or at SDF_HISTORY_FEATURE_WEIGHT_CAP while the pixel carries a feature.
// The stationary window (historyWindowRW, SDF_HISTORY_WINDOW_WORDS a pixel in the same planar layout) is one buffer the
// resolve updates in place, not history in two copies: word zero is the phase mask, with the sum of the phases' packed
// weights above it (sdfWindowSampleWeight), and from SDF_HISTORY_WINDOW_OFFSET
// each phase holds three words (sdfPackWindowSample). A completed window replaces each phase with that phase's new samples. Its fixed-order sum is invariant when the
// jitter repeats; an exponential mean capped at one period instead keeps favoring the latest phase, and clipping to its
// neighborhood destroys unsighted features. A complete window is summed in full once a period and otherwise keeps its
// mean and replaces one phase's term in it (sdfResolveTemporal). Every window access names the invocation's own pixel (sdfHistoryWindowWord
// takes no other), and a pixel keeps its window only while its history comes from that same pixel, so no invocation
// reads a word another invocation of the dispatch writes; a pixel whose history comes from elsewhere restarts its
// window at this frame's phase, and the phases its mask leaves out are never read.
// The falloff of the Gaussian a sample is weighted by, over its squared distance in output pixels from the output
// pixel's center: 1 / (2 * 0.35^2), a reconstruction about as wide as one output pixel's box.
static const float SdfResolveFilterFalloff = 4.08;
// A render sample is a sub-pixel feature when, in at least three of the four directions through it (across, down and
// the two diagonals), both of its neighbours lie this share of the brighter luma below it, or it below them: a dot in all
// four, a line thinner than a sample in the three its own direction is not. The corner of an edge keeps a neighbour on its
// own side in at least two directions, so an edge sweeping across a surface, a shadow's, never counts.
static const float SdfResolveFeatureContrast = 0.5;
// The most, in output pixels, a pixel's history may have moved and still carry its remembered feature into the box: a
// feature moves with its surface through the reprojection, but one that moves over a surface, a glint under a turning
// camera, must be clipped like any change.
static const float SdfResolveFeatureTravel = 0.05;

// One phase of the stationary window, SDF_HISTORY_SAMPLE_WORDS words: its color as a shared-exponent word
// (Shared/shared-exponent.hlsli); its coverage as UNORM10 in bits 0 to 9, which holds full coverage exactly, its Gaussian
// weight over [0, 2) as UNORM10 in bits 10 to 19, never zero for a sample that is kept, and the high twelve bits of its
// transport's inverse distance above them; then the transport's fog, haze and medium weights, each a share in [0, 1], as
// UNORM10, and the inverse distance's low two bits. The inverse distance is its half float's fifteen bits (sdfPackTransport)
// less the lowest, a relative 2^-9. A phase is read back as it was written every frame, so the rounding is the same
// each time the window sums and nothing in it follows the jitter.
#if SDF_HISTORY_SAMPLE_WORDS != 3
#error A stationary phase packs into three words.
#endif
uint3 sdfPackWindowSample(float4 color, float weight, float4 transport) {
    uint distance = ((f32tof16(min(transport.y, 65504.0)) & 0x7FFFu) >> 1u);
    uint3 air = (uint3)round(saturate(transport.xzw) * 1023.0);
    uint coverage = (uint)round(saturate(color.a) * 1023.0);
    uint weighed = max((uint)round(saturate(weight * 0.5) * 1023.0), 1u);

    return uint3(
        puckPackSharedExponent(color.rgb),
        (coverage | (weighed << 10u) | ((distance >> 2u) << 20u)),
        (air.x | (air.y << 10u) | (air.z << 20u) | ((distance & 3u) << 30u))
    );
}
void sdfUnpackWindowSample(uint3 packed, out float4 color, out float weight, out float4 transport) {
    uint distance = (((packed.y >> 20u) << 2u) | (packed.z >> 30u));

    color = float4(puckUnpackSharedExponent(packed.x), (float(packed.y & 0x3FFu) / 1023.0));
    weight = ((float((packed.y >> 10u) & 0x3FFu) / 1023.0) * 2.0);
    transport = float4(
        (float(packed.z & 0x3FFu) / 1023.0),
        f16tof32(distance << 1u),
        (float((packed.z >> 10u) & 0x3FFu) / 1023.0),
        (float((packed.z >> 20u) & 0x3FFu) / 1023.0)
    );
}

// A packed phase's weight field (sdfPackWindowSample), and the weight a sum of such fields stands for, as
// sdfUnpackWindowSample decodes one field.
uint sdfWindowSampleWeight(uint3 packed) {
    return ((packed.y >> 10u) & 0x3FFu);
}
float sdfWindowWeight(uint weights) {
    return ((float(weights) / 1023.0) * 2.0);
}

uint2 sdfResolveClamp(int2 pixel, uint2 dims) {
    return (uint2)clamp(pixel, int2(0, 0), (int2(dims) - 1));
}

// The address of stationary window word `word` of the invocation at `id` in an output of `extent`. The window is
// updated in place, so it is addressed only by the invocation's own pixel, never a reprojected one.
uint sdfHistoryWindowWord(uint3 id, uint word, uint2 extent) {
    return sdfHistoryWord(((id.y * extent.x) + id.x), word, extent);
}
float3 sdfToYCoCg(float3 color) {
    return float3(
        ((0.25 * color.r) + (0.5 * color.g) + (0.25 * color.b)),
        ((0.5 * color.r) - (0.5 * color.b)),
        ((-0.25 * color.r) + (0.5 * color.g) - (0.25 * color.b))
    );
}
float3 sdfFromYCoCg(float3 color) {
    float t = (color.x - color.z);

    return float3((t + color.y), (color.x + color.z), (t - color.y));
}
// Projects each component to its measured interval. A thin chroma interval must not amplify a half-float rounding
// difference into a luminance change: moving toward the box's center couples those otherwise independent corrections.
float3 sdfClipToBox(float3 color, float3 boxMin, float3 boxMax) {
    return clamp(color, boxMin, boxMax);
}
// What the resolve reads of one render sample, at a render-grid position clamped to the grid, each part formed once a
// sample for every footprint that reads it: its lit color, its luma, its reactivity, its surface (the identity, zero for
// a sample with no surface, the ray distance, and the tangent plane: the normal with the distance times the cosine its
// own jittered ray meets it at, which carries the plane to any other ray, sdfResolveReceiverDistance) and its transport:
// the atmosphere's in-scatter weights and the scaled inverse distance along the sample's own jittered ray, the ray its
// color's transmittance was taken along, scaled by its coverage (sdfSampleTransport), zero with no coverage or surface.
struct SdfResolveSample {
    float4 color;
    float luma;
    float reactive;
    bool hit;
    uint identity;
    float t;
    float4 plane;
    float4 transport;
};
// The render samples an output group's footprints read, loaded once for the group (sdfResolveTileFill): the samples from
// sdfResolveTileOrigin, SdfResolveTileEdge a side. The tile holds every sample any footprint of the group reads, each
// tap's eight feature neighbours included, whenever the render grid is no larger than the output on either axis: the
// group's centers span at most eight samples from floor(low), its taps reach two samples before and two after that span,
// and the tile holds two before and four after it. The host never renders above the output (RenderGraphExtent.Quantize
// caps every render fraction at one), so the resolve reads its samples from the tile alone and never from their
// resources, which keeps one copy of the sample load in the kernel rather than one a tap.
static const int SdfResolveTileEdge = 13;
groupshared uint2 sdfResolveTileColor[SdfResolveTileEdge * SdfResolveTileEdge];
groupshared float4 sdfResolveTileTransport[SdfResolveTileEdge * SdfResolveTileEdge];
groupshared float4 sdfResolveTilePlane[SdfResolveTileEdge * SdfResolveTileEdge];
// A sample's identity, zero with no surface, and its ray distance's bits.
groupshared uint2 sdfResolveTileSurface[SdfResolveTileEdge * SdfResolveTileEdge];
groupshared float sdfResolveTileReactive[SdfResolveTileEdge * SdfResolveTileEdge];
// Each tile sample's luma, and its sub-pixel feature contrast (sdfResolveFeature) where all eight of its neighbours lie in
// the tile: the contrast for a feature, negative for a sample that is none. Computed once a sample for the group, so a
// pixel's nine taps read their features rather than each reading its eight neighbours.
groupshared float sdfResolveTileLuma[SdfResolveTileEdge * SdfResolveTileEdge];
groupshared float sdfResolveTileFeature[SdfResolveTileEdge * SdfResolveTileEdge];
static int2 sdfResolveTileOrigin = int2(0, 0);

// Reads one render sample from its own resources.
SdfResolveSample sdfResolveSampleLoad(int2 position, uint2 render) {
    uint2 pixel = sdfResolveClamp(position, render);
    uint record = sdfVisibilityRecord(pixel, 0u, render);
    SdfVisibility visibility = sdfLoadVisibility(record);
    SdfResolveSample sample;

    sample.color = currentColor.Load(int3(pixel, 0));
    sample.luma = sdfToYCoCg(sample.color.rgb).x;
    sample.reactive = reactivity[sdfReactivityIndex(pixel, 0u, render)];
    sample.hit = sdfVisibilityHit(visibility);
    sample.identity = (sample.hit ? visibility.identity : 0u);
    sample.t = visibility.t;
    sample.plane = float4(0.0, 0.0, 0.0, 0.0);
    sample.transport = float4(0.0, 0.0, 0.0, 0.0);
    if (sample.hit) {
        float3 direction = cameraRayDirection(worldView(), ((float2(pixel) + 0.5) / float2(render)));
        float3 normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);

        sample.plane = float4(normal, (sample.t * dot(normal, direction)));
        if ((sample.t > 0.0) && (sample.color.a > 0.0)) {
            SdfAir air = sdfAirAlong(passGroup.viewPosition, direction, sample.t, true);

            sample.transport = (sample.color.a * float4(air.weights.x, (SDF_TRANSPORT_INVERSE_DISTANCE_SCALE / sample.t), air.weights.y, air.weights.z));
        }
    }
    return sample;
}
// The feature test (SdfResolveFeatureContrast) of a sample of luma `center` over its four pairs of neighbour lumas: across,
// down and the two diagonals, `a` one side of it and `b` the other. A dot is a feature in all four directions, a line
// thinner than a sample in the three its own direction is not; the corner of an edge keeps a neighbour on its own side in
// at least two, so an edge never counts. `contrast` is its luma against the eight neighbours' mean.
bool sdfResolveFeatureTest(float center, float4 a, float4 b, out float contrast) {
    float4 high = max(a, b);
    float4 low = min(a, b);
    uint above = dot(uint4((center - high) > (SdfResolveFeatureContrast * center)), uint4(1u, 1u, 1u, 1u));
    uint below = dot(uint4((low - center) > (SdfResolveFeatureContrast * low)), uint4(1u, 1u, 1u, 1u));

    contrast = abs(center - (dot((a + b), float4(1.0, 1.0, 1.0, 1.0)) * 0.125));

    return ((above >= 3u) || (below >= 3u));
}
// The feature test of the tile sample at `local`, whose eight neighbours all lie in the tile, from the tile's lumas.
bool sdfResolveFeatureOf(float center, int2 local, out float contrast) {
    int at = ((local.y * SdfResolveTileEdge) + local.x);
    float4 a = float4(sdfResolveTileLuma[at + 1], sdfResolveTileLuma[at + SdfResolveTileEdge],
        sdfResolveTileLuma[(at + SdfResolveTileEdge) + 1], sdfResolveTileLuma[(at + SdfResolveTileEdge) - 1]);
    float4 b = float4(sdfResolveTileLuma[at - 1], sdfResolveTileLuma[at - SdfResolveTileEdge],
        sdfResolveTileLuma[(at - SdfResolveTileEdge) - 1], sdfResolveTileLuma[(at - SdfResolveTileEdge) + 1]);

    return sdfResolveFeatureTest(center, a, b, contrast);
}
// Loads the group's tile. Every lane of the group calls it before any lane reads the tile, and it ends with the group
// barrier that publishes the tile and the features it computes from it. `first` is the group's first output pixel.
void sdfResolveTileFill(uint2 first, uint lane, uint2 extent, uint2 render) {
    // The footprints of the group's pixels at any jitter: the 3x3 taps and their 5x5 feature windows about the nearest
    // sample, floor(center - jitter), and the spatial path's 4x4 taps from floor(position) - 1, position = center - 0.5
    // less the jitter or none, centers from the group's first to its last pixel.
    float2 scale = (float2(render) / float2(extent));
    float2 low = (((float2(first) + 0.5) * scale) - 0.5);

    sdfResolveTileOrigin = (int2(floor(low)) - 2);
    [loop] for (uint entry = lane; entry < (uint)(SdfResolveTileEdge * SdfResolveTileEdge); entry += 64u) {
        int2 at = (sdfResolveTileOrigin + int2((int)(entry % (uint)SdfResolveTileEdge), (int)(entry / (uint)SdfResolveTileEdge)));
        SdfResolveSample sample = sdfResolveSampleLoad(at, render);

        sdfResolveTileColor[entry] = uint2((f32tof16(sample.color.r) | (f32tof16(sample.color.g) << 16u)), (f32tof16(sample.color.b) | (f32tof16(sample.color.a) << 16u)));
        sdfResolveTileTransport[entry] = sample.transport;
        sdfResolveTilePlane[entry] = sample.plane;
        sdfResolveTileSurface[entry] = uint2(sample.identity, asuint(sample.t));
        sdfResolveTileReactive[entry] = sample.reactive;
        sdfResolveTileLuma[entry] = sample.luma;
    }
    GroupMemoryBarrierWithGroupSync();
    [loop] for (uint entry = lane; entry < (uint)(SdfResolveTileEdge * SdfResolveTileEdge); entry += 64u) {
        int2 local = int2((int)(entry % (uint)SdfResolveTileEdge), (int)(entry / (uint)SdfResolveTileEdge));
        float feature = -1.0;

        if (all(local >= 1) && all(local < (SdfResolveTileEdge - 1))) {
            float contrast;

            feature = (sdfResolveFeatureOf(sdfResolveTileLuma[entry], local, contrast) ? contrast : -1.0);
        }
        sdfResolveTileFeature[entry] = feature;
    }
    GroupMemoryBarrierWithGroupSync();
}
// The tile entry of a render-grid position. The tile holds every position the group reads; the clamp keeps an index in
// the tile whatever a caller passes.
uint sdfResolveTileEntry(int2 position) {
    int2 local = clamp((position - sdfResolveTileOrigin), int2(0, 0), int2((SdfResolveTileEdge - 1), (SdfResolveTileEdge - 1)));

    return (uint)((local.y * SdfResolveTileEdge) + local.x);
}
// One render sample at a render-grid position, from the group's tile. The lit image is half-float
// (RenderGraphPackageCatalog.WorkingFormat), so the tile's packed color is the color loaded.
SdfResolveSample sdfResolveSampleAt(int2 position) {
    uint entry = sdfResolveTileEntry(position);
    uint2 color = sdfResolveTileColor[entry];
    uint2 surface = sdfResolveTileSurface[entry];
    SdfResolveSample sample;

    sample.color = float4(f16tof32(color.x & 0xFFFFu), f16tof32(color.x >> 16u), f16tof32(color.y & 0xFFFFu), f16tof32(color.y >> 16u));
    sample.luma = sdfResolveTileLuma[entry];
    sample.reactive = sdfResolveTileReactive[entry];
    sample.identity = surface.x;
    sample.t = asfloat(surface.y);
    sample.hit = (surface.x != 0u);
    sample.plane = sdfResolveTilePlane[entry];
    sample.transport = sdfResolveTileTransport[entry];
    return sample;
}
// A sample's receiver distance along `receiverDirection` (sdfHistoryReceiverDistance): its tangent plane carried to that
// ray, or its own ray distance where the ray has no finite, forward intersection within the view's reach.
float sdfResolveReceiverDistance(SdfResolveSample sample, float3 receiverDirection) {
    float facing = dot(sample.plane.xyz, receiverDirection);

    if (abs(facing) <= 1.0e-6) {
        return sample.t;
    }

    float t = (sample.plane.w / facing);

    return ((isfinite(t) && (t > 0.0) && (t <= passGroup.farDistance)) ? t : sample.t);
}
// Whether the render sample at a position is a sub-pixel feature (SdfResolveFeatureContrast) against its eight
// neighbours, and its contrast against their mean, as the tile computed it (sdfResolveTileFill): every tap the group
// reads lies within the tile's border, so its neighbours do too.
bool sdfResolveFeature(int2 position, out float contrast) {
    float stored = sdfResolveTileFeature[sdfResolveTileEntry(position)];

    contrast = max(stored, 0.0);
    return (stored >= 0.0);
}
// The history at a continuous output position, the first texel's center at zero, each texel clamped to the image: the
// color Catmull-Rom over the sixteen nearest texels of the history color, the transport bilinear over the four nearest
// of the history surface. The transport (each atmosphere kind's weight and the inverse distance) is smooth across a
// surface and is clipped to this frame's box after, so it takes no sharpening filter.
void sdfHistoryAt(float2 position, uint2 dims, out float4 color, out float4 transport) {
    float2 origin = floor(position);
    float2 f = (position - origin);
    float4 wx = puckCatmullRomWeights(f.x);
    float4 wy = puckCatmullRomWeights(f.y);
    int2 corner = (int2(origin) - 1);

    color = float4(0.0, 0.0, 0.0, 0.0);
    transport = float4(0.0, 0.0, 0.0, 0.0);
    [unroll] for (int y = 0; y < 4; y++) {
        float4 rowColor = float4(0.0, 0.0, 0.0, 0.0);

        [unroll] for (int x = 0; x < 4; x++) {
            rowColor += (wx[x] * historyColor.Load(int3(sdfResolveClamp((corner + int2(x, y)), dims), 0)));
        }
        color += (wy[y] * rowColor);
    }
    [unroll] for (int y = 0; y < 2; y++) {
        [unroll] for (int x = 0; x < 2; x++) {
            uint2 texel = sdfResolveClamp((int2(origin) + int2(x, y)), dims);
            uint pixel = ((texel.y * dims.x) + texel.x);
            float weight = (((x == 0) ? (1.0 - f.x) : f.x) * ((y == 0) ? (1.0 - f.y) : f.y));

            transport += (weight * sdfUnpackTransport(uint2(historySurface[sdfHistoryWord(pixel, 2u, dims)], historySurface[sdfHistoryWord(pixel, 3u, dims)])));
        }
    }
}
// The view's own camera rows, unjittered, as sdfProjectView reads a view.
void sdfCurrentViewRows(out float4 rows[6]) {
    rows[0] = float4(passGroup.viewPosition, 1.0);
    rows[1] = float4(passGroup.viewRight, passGroup.tanHalfFieldOfView);
    rows[2] = float4(passGroup.viewUp, passGroup.aspectRatio);
    rows[3] = float4(passGroup.viewForward, 0.0);
    rows[4] = float4((float2)passGroup.imageExtent, 0.0, 0.0);
    rows[5] = float4(passGroup.nearDistance, passGroup.frustumOffset, 0.0);
}
// The output pixel's unjittered ray, as the composite's sdfSkyPassDirection takes it: the ray each tap's receiver plane is
// measured along, and an exact copy's transport.
float3 sdfResolveAirDirection(uint2 pixel, uint2 extent) {
    ViewportData view = worldView();

    view.lens.yz = passGroup.frustumOffset;

    return cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(extent)));
}
// The spatial path: the lit color and its transport at a continuous render-grid position, each reconstructed from the
// same taps with one set of weights, and the transport word the composite reads; or, where the output has the grid's
// extent and no jitter, the render pixel itself, whose word is its sample's ray distance (sdfTransportSampleWord), read
// from the record exactly where a native view's composite reads it.
void sdfResolveSpatial(uint2 pixel, float2 position, bool exact, uint2 render, float3 origin, float3 direction, out float4 color, out float4 transport, out uint2 word) {
    uint4 current = uint4(0u, 0u, render);

    if (exact) {
        SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, render));
        float t = (sdfVisibilityHit(visibility) ? visibility.t : 0.0);

        color = puckReconstructionTapWithin(currentColor, int2(pixel), render, uint2(0, 0), current);
        transport = sdfSampleTransport(color.a, t, origin, direction);
        word = sdfTransportSampleWord(t);
        return;
    }

    PuckReconstructionFootprint footprint = puckReconstructionFootprintAt(position, render, passGroup.upscaleSharpness);
    PuckReconstructionSum colors = puckReconstructionSumStart();
    PuckReconstructionSum transports = puckReconstructionSumStart();

    [unroll] for (uint tap = 0u; tap < 16u; tap++) {
        float4 tapColor = float4(0.0, 0.0, 0.0, 0.0);
        float4 tapTransport = float4(0.0, 0.0, 0.0, 0.0);

        if (puckReconstructionReads(footprint, tap)) {
            int2 at = (footprint.origin + puckReconstructionTapOffset(tap));

            // The whole grid is current, so the tap's color is its clamped sample's (puckReconstructionTapWithin).
            SdfResolveSample sample = sdfResolveSampleAt(at);

            tapColor = sample.color;
            tapTransport = sample.transport;
        }
        puckReconstructionAdd(footprint, tap, tapColor, colors);
        puckReconstructionAdd(footprint, tap, tapTransport, transports);
    }
    color = puckReconstructionFinish(footprint, colors);
    transport = puckReconstructionFinish(footprint, transports);
    word = sdfPackTransport(transport);
}

// The temporal path: this frame's 3x3 samples joined by the history the pixel's motion reaches, which it writes back.
// `accepted` says the history survived, `total` is the weight the mean `accumulated` (and its transport) gathered, and
// `reactive` is the reactivity of the tap nearest the pixel's center: the pixel's own, so a steady pixel beside a reactive
// one keeps its history.
void sdfResolveTemporal(uint3 id, uint2 extent, uint2 render, float2 jitter, float3 airDirection, out bool accepted, out bool complete, out float total, out float reactive, out float4 accumulated, out float4 accumulatedTransport, out bool gathered, out float4 current, out float4 currentTransport) {
    ViewportData view = worldView();
    float2 scale = (float2(extent) / float2(render));
    float2 center = ((float2(id.xy) + 0.5) / scale);
    int2 nearest = int2(floor(center - jitter));
    float4 sum = float4(0.0, 0.0, 0.0, 0.0);
    float4 transportSum = float4(0.0, 0.0, 0.0, 0.0);
    float weightSum = 0.0;

    reactive = 0.0;
    float3 boxMin = float3(3.402823e+38, 3.402823e+38, 3.402823e+38);
    float3 boxMax = -boxMin;
    float coverageMin = 1.0;
    float coverageMax = 0.0;
    float4 transportMin = float4(3.402823e+38, 3.402823e+38, 3.402823e+38, 3.402823e+38);
    float4 transportMax = float4(0.0, 0.0, 0.0, 0.0);
    bool hit = false;
    float hitT = 0.0;
    uint hitIdentity = 0u;
    uint hitRecord = 0u;
    uint2 hitPixel = uint2(0u, 0u);
    // Each 3x3 tap's identity (0 on a miss) and tangent-plane distance along the output ray, and the receiver history keeps:
    // the tap nearest its center that is not a feature, so a feature the jitter leaves next frame does not reject a
    // history the surface under it carries. Validating against every tap keeps an edge's history whichever side of it the
    // jitter puts the nearest-depth tap on; the clip below still bounds what the history contributes. The feature the
    // pixel shows this frame is the most contrasting of its unreactive feature taps.
    float4 currentRows[6];
    sdfCurrentViewRows(currentRows);
    // Jitter changes which side of a static silhouette the footprint samples. Such a footprint has no single
    // receiver identity shared by every phase. Prove that the camera and every visible tap stand still instead.
    bool footprintStill = ((passGroup.historyFrames != 0u) && (passGroup.previousView[0].w != 0.0) &&
        all(currentRows[0].xyz == passGroup.previousView[0].xyz) &&
        all(currentRows[1] == passGroup.previousView[1]) && all(currentRows[2] == passGroup.previousView[2]) &&
        all(currentRows[3].xyz == passGroup.previousView[3].xyz) &&
        all(currentRows[4].xy == passGroup.previousView[4].xy) && all(currentRows[5].xyz == passGroup.previousView[5].xyz));
    uint identities[9];
    float distances[9];
    float centerWeight = -1.0;
    uint centerIdentity = 0u;
    float centerT = 0.0;
    float nearestWeight = -1.0;
    uint nearestIdentity = 0u;
    float nearestT = 0.0;
    bool sighted = false;
    float2 axisContrast = 0.0;
    float sightedContrast = 0.0;
    float3 sightedYCoCg = float3(0.0, 0.0, 0.0);

    [unroll] for (int dy = -1; dy <= 1; dy++) {
        [unroll] for (int dx = -1; dx <= 1; dx++) {
            int2 position = (nearest + int2(dx, dy));
            uint2 pixel = sdfResolveClamp(position, render);
            uint tap = ((uint)((dy + 1) * 3) + (uint)(dx + 1));
            SdfResolveSample sample = sdfResolveSampleAt(position);
            float4 color = sample.color;
            float2 offset = (((float2(pixel) + 0.5 + jitter) - center) * scale);
            float weight = exp(-SdfResolveFilterFalloff * dot(offset, offset));
            float3 ycocg = sdfToYCoCg(color.rgb);
            float4 transport = sample.transport;
            float contrast;
            bool feature = sdfResolveFeature(position, contrast);
            if (passGroup.viewsRate.x > 1u) {
                axisContrast = max(axisContrast, abs(sample.luma - float2(sdfResolveTileLuma[sdfResolveTileEntry(position + int2(1, 0))],
                    sdfResolveTileLuma[sdfResolveTileEntry(position + int2(0, 1))])));
            }

            float tapReactive = sample.reactive;
            footprintStill = (footprintStill && (tapReactive <= 0.0));

            uint record = sdfVisibilityRecord(pixel, 0u, render);
            bool surface = sample.hit;
            float receiverDistance = 0.0;

            if (surface) {
                receiverDistance = sdfResolveReceiverDistance(sample, airDirection);
            }

            identities[tap] = sample.identity;
            distances[tap] = receiverDistance;
            if (weight > nearestWeight) {
                nearestWeight = weight;
                reactive = saturate(tapReactive);
                nearestIdentity = identities[tap];
                nearestT = distances[tap];
            }
            if (!feature && (weight > centerWeight)) {
                centerWeight = weight;
                centerIdentity = identities[tap];
                centerT = distances[tap];
            }
            if (feature && (tapReactive <= 0.0) && (contrast > sightedContrast)) {
                sighted = true;
                sightedContrast = contrast;
                sightedYCoCg = ycocg;
            }
            if (surface && (!hit || (sample.t < hitT))) {
                hit = true;
                hitT = sample.t;
                hitIdentity = sample.identity;
                hitRecord = record;
                hitPixel = pixel;
            }
            sum += (weight * color);
            transportSum += (weight * transport);
            weightSum += weight;
            boxMin = min(boxMin, ycocg);
            boxMax = max(boxMax, ycocg);
            coverageMin = min(coverageMin, color.a);
            coverageMax = max(coverageMax, color.a);
            transportMin = min(transportMin, transport);
            transportMax = max(transportMax, transport);
        }
    }
    if (centerWeight < 0.0) {
        centerIdentity = nearestIdentity;
        centerT = nearestT;
    }

    accepted = false;
    complete = false;
    uint pixelIndex = ((id.y * extent.x) + id.x);
    uint historyPixel = pixelIndex;
    float2 historyPosition = float2(0.0, 0.0);
    float historyWeightGathered = 0.0;
    uint receiverIdentity = 0u;
    float receiverT = 0.0;
    // The feature the history remembers for this pixel, with the frames it has left, and how far, in output pixels, the
    // history moved to reach the pixel.
    float3 featureYCoCg = float3(0.0, 0.0, 0.0);
    uint featureLife = 0u;
    float travel = 1.0e9;

    if ((passGroup.historyFrames != 0u) && (passGroup.previousView[0].w != 0.0)) {
        bool moved = false;
        float2 previousPixel = float2(0.0, 0.0);
        float2 currentPixel = float2(0.0, 0.0);
        float previousT = 0.0;

        if (hit) {
            // The point the nearest-depth sample's own jittered ray reached, carried by its shape or triangle.
            float3 direction = cameraRayDirection(view, ((float2(hitPixel) + 0.5) / float2(render)));
            float3 surfacePoint = (view.position.xyz + (direction * hitT));
            float3 previousPoint;

            if (sdfPreviousPoint(hitRecord, sdfLoadVisibility(hitRecord), surfacePoint, previousPoint)) {
                float3 relative = (previousPoint - passGroup.previousView[0].xyz);

                previousT = length(relative);
                moved = (sdfProjectView(passGroup.previousView, relative, previousPixel) &&
                    sdfProjectView(currentRows, (surfacePoint - view.position.xyz), currentPixel));
            }
        } else {
            // An uncovered pixel holds nothing the camera's translation moves, so it moves with the camera's rotation alone.
            view.lens.yz = passGroup.frustumOffset;
            float3 direction = (cameraRayDirection(view, ((float2(id.xy) + 0.5) / float2(extent))) * passGroup.farDistance);

            moved = (sdfProjectView(passGroup.previousView, direction, previousPixel) &&
                sdfProjectView(currentRows, direction, currentPixel));
        }
        if (moved) {
            float2 motion = ((previousPixel / passGroup.previousView[4].xy) - (currentPixel / float2(render)));

            historyPosition = (((float2(id.xy) + 0.5) + (motion * float2(extent))) - 0.5);
            travel = length(motion * float2(extent));
            if (all(historyPosition >= -0.5) && all(historyPosition <= (float2(extent) - 0.5))) {
                uint2 texel = sdfResolveClamp(int2(floor(historyPosition + 0.5)), extent);
                historyPixel = ((texel.y * extent.x) + texel.x);
                uint historyIdentity = historySurface[sdfHistoryWord(historyPixel, 0u, extent)];
                uint distanceAndWeight = historySurface[sdfHistoryWord(historyPixel, 1u, extent)];
                float historyT = f16tof32(distanceAndWeight & 0xFFFFu);
                uint featureWordA = historySurface[sdfHistoryWord(historyPixel, 4u, extent)];
                uint featureWordB = historySurface[sdfHistoryWord(historyPixel, 5u, extent)];

                // A receiver-plane distance in the preceding view, shifted by the camera's move along the nearest hit.
                float shift = (hit ? (previousT - hitT) : 0.0);

                historyWeightGathered = f16tof32(distanceAndWeight >> 16u);
                featureYCoCg = float3(f16tof32(featureWordA & 0xFFFFu), f16tof32(featureWordA >> 16u), f16tof32(featureWordB & 0xFFFFu));
                featureLife = ((featureWordB & SdfHistoryFeatureMask) >> 16u);
                [unroll] for (uint tap = 0u; tap < 9u; tap++) {
                    bool matches = ((identities[tap] == 0u)
                        ? (historyIdentity == 0u)
                        : sdfHistoryReceiverMatches(identities[tap], historyIdentity, (distances[tap] + shift), historyT));
                    if (matches && (!accepted || (abs((distances[tap] + shift) - historyT) < abs((receiverT + shift) - historyT)))) {
                        receiverIdentity = identities[tap];
                        receiverT = distances[tap];
                    }
                    accepted = (accepted || matches);
                }
            }
        }
    }

    // The feature the pixel remembers widens the clip and raises the weight cap while its surface stands still.
    bool remembered = (accepted && hit && (featureLife != 0u) && (travel <= SdfResolveFeatureTravel) && all(isfinite(featureYCoCg)));
    float weightCap = (remembered ? SDF_HISTORY_FEATURE_WEIGHT_CAP : SDF_HISTORY_WEIGHT_CAP);
    // Stationary receivers retain one bounded window, not an ever older history. This also follows changes in lighting:
    // every phase is replaced within a period, without clamping its other phases to a neighborhood that jitter moved.
    // Moving or reactive receivers restart the window and keep the ordinary clipped, reprojected resolve while it fills.
    uint phase = (passGroup.historyFrames % SDF_TEMPORAL_PERIOD);
    uint fullMask = ((1u << SDF_TEMPORAL_PERIOD) - 1u);
    // The ordinary receiver check already covers the interior. Only a rejected footprint needs the more expensive
    // proof that every tap is stationary before retaining a window across a phase's different receiver selection.
    footprintStill = (footprintStill && !accepted);
    // A tap stands still when its draw has not moved since the preceding frame (sdfMeshDrawUnmoved), which proves its
    // previous point is its current one without reading the triangle: a moved draw counts as moving even where one of
    // its triangles held still, which only declines the window there.
    [branch] if (footprintStill) {
        [loop] for (uint tap = 0u; tap < 9u; tap++) {
            uint2 pixel = sdfResolveClamp((nearest + int2((int)(tap % 3u) - 1, (int)(tap / 3u) - 1)), render);
            uint record = sdfVisibilityRecord(pixel, 0u, render);
            SdfVisibility visibility = sdfLoadVisibility(record);
            SdfMeshHit tapHit;

            if (sdfVisibilityHit(visibility)) {
                footprintStill = (footprintStill &&
                    sdfMeshResolveHit(sdfVisibilitySource(visibility.identity), sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord], tapHit) &&
                    sdfMeshDrawUnmoved(tapHit.record));
            }
        }
    }
    // The window is this pixel's own, updated in place: it carries over only while the history comes from this pixel.
    bool stationary = ((footprintStill || (accepted && (travel <= SdfResolveFeatureTravel) && (reactive <= 0.0))) && (historyPixel == pixelIndex));
    // Keep the receiver that carries a stationary window while the footprint still sees it. Picking the nearest
    // non-feature anew every phase can switch to a thin adjoining surface that the following phase misses entirely,
    // rejecting a valid window at an otherwise stationary edge.
    if (stationary && accepted) {
        centerIdentity = receiverIdentity;
        centerT = receiverT;
    }
    bool sampleValid = ((reactive <= 0.0) && (weightSum > 1.0e-6) && isfinite(weightSum) && all(isfinite(sum)) && all(isfinite(transportSum)));
    // A pixel that is not stationary keeps no phase, this frame's included: its window starts from the first frame it
    // stands still, so a pixel in motion reads the mask and writes nothing of the window.
    uint storedWord = historyWindowRW[sdfHistoryWindowWord(id, 0u, extent)];
    uint storedMask = (storedWord & fullMask);
    uint mask = ((stationary && sampleValid) ? (storedMask | (1u << phase)) : 0u);

    gathered = ((weightSum > 1.0e-6) && isfinite(weightSum) && all(isfinite(sum)) && all(isfinite(transportSum)));
    current = (gathered ? (sum / weightSum) : float4(0.0, 0.0, 0.0, 0.0));
    current.a = clamp(current.a, coverageMin, coverageMax);
    currentTransport = (gathered ? (transportSum / weightSum) : float4(0.0, 0.0, 0.0, 0.0));
    float windowWeight = 0.0;
    // The window's mean is the weighted mean of its phases' packed words. A window that was complete before this frame
    // keeps that mean, stored at this pixel by the frame before (the color in the history color, the transport in the
    // history surface; a full stored mask says that frame was reusable, so it stored them unzeroed), and the sum of its
    // phases' packed weights above the mask in the window's first word. Replacing one phase then changes the mean by
    // that phase's weight share of the difference between its new sample and its old one, which costs the pixel one
    // phase's words where summing the window again costs all eight:
    // - a component the phase did not change is carried exactly, since its difference is zero, so a still pixel under
    //   steady light shows the same bits every frame and full coverage stays full;
    // - the stored mean is a half float, so a phase that did change leaves the mean within an ulp of the full sum's.
    //   Every group of pixels sums its windows in full once a period, on its own frame of the period, so that rounding
    //   never gathers past a period. The turn is the group's, never the pixel's: a wave runs both paths when its lanes
    //   differ, and a turn that differed by pixel would put a summing lane in nearly every wave;
    // - a phase whose weight changed (the footprint's coverage did) sums in full, since the share rests on the weights'
    //   sum standing.
    uint phaseOffset = (SDF_HISTORY_WINDOW_OFFSET + (phase * SDF_HISTORY_SAMPLE_WORDS));
    uint3 phasePacked = uint3(0u, 0u, 0u);
    uint3 phaseStored = uint3(0u, 0u, 0u);
    uint weights = 0u;
    bool replaces = false;

    if ((mask & (1u << phase)) != 0u) {
        float4 color = (sum / weightSum);

        color.a = clamp(color.a, coverageMin, coverageMax);
        phasePacked = sdfPackWindowSample(color, weightSum, (transportSum / weightSum));
        phaseStored = uint3(historyWindowRW[sdfHistoryWindowWord(id, phaseOffset, extent)],
            historyWindowRW[sdfHistoryWindowWord(id, (phaseOffset + 1u), extent)],
            historyWindowRW[sdfHistoryWindowWord(id, (phaseOffset + 2u), extent)]);
        if (any(phaseStored != phasePacked)) {
            historyWindowRW[sdfHistoryWindowWord(id, phaseOffset, extent)] = phasePacked.x;
            historyWindowRW[sdfHistoryWindowWord(id, (phaseOffset + 1u), extent)] = phasePacked.y;
            historyWindowRW[sdfHistoryWindowWord(id, (phaseOffset + 2u), extent)] = phasePacked.z;
        }

        uint2 group = (id.xy / 8u);
        bool turn = ((((passGroup.historyFrames + group.x) + (3u * group.y)) % SDF_TEMPORAL_PERIOD) == 0u);

        replaces = ((storedMask == fullMask) && !turn && (sdfWindowSampleWeight(phaseStored) == sdfWindowSampleWeight(phasePacked)));
    }
    [branch] if (replaces) {
        float4 color;
        float4 colorStored;
        float weight;
        float weightStored;
        float4 transport;
        float4 transportStored;

        sdfUnpackWindowSample(phasePacked, color, weight, transport);
        sdfUnpackWindowSample(phaseStored, colorStored, weightStored, transportStored);
        weights = (storedWord >> SDF_TEMPORAL_PERIOD);
        windowWeight = sdfWindowWeight(weights);

        float share = (weight / windowWeight);

        complete = true;
        accepted = true;
        total = windowWeight;
        accumulated = (historyColor.Load(int3(id.xy, 0)) + (share * (color - colorStored)));
        accumulatedTransport = (sdfUnpackTransport(uint2(historySurface[sdfHistoryWord(pixelIndex, 2u, extent)], historySurface[sdfHistoryWord(pixelIndex, 3u, extent)])) +
            (share * (transport - transportStored)));
    } else {
        float4 windowColor = 0.0;
        float4 windowTransport = 0.0;
        float4 windowMin = 3.402823e+38;
        float4 windowMax = -windowMin;

        [unroll] for (uint sample = 0u; sample < SDF_TEMPORAL_PERIOD; sample++) {
            uint offset = (SDF_HISTORY_WINDOW_OFFSET + (sample * SDF_HISTORY_SAMPLE_WORDS));
            bool present = ((mask & (1u << sample)) != 0u);

            // Decode the new phase too: using its unrounded sum while the other phases use their packed words would
            // itself pulse.
            if (present) {
                uint3 packed = ((sample == phase)
                    ? phasePacked
                    : uint3(historyWindowRW[sdfHistoryWindowWord(id, offset, extent)],
                        historyWindowRW[sdfHistoryWindowWord(id, (offset + 1u), extent)],
                        historyWindowRW[sdfHistoryWindowWord(id, (offset + 2u), extent)]));
                float4 color;
                float weight;
                float4 transport;

                sdfUnpackWindowSample(packed, color, weight, transport);
                windowMin = min(windowMin, color);
                windowMax = max(windowMax, color);
                windowColor += (weight * color);
                windowTransport += (weight * transport);
                weights += sdfWindowSampleWeight(packed);
            }
        }
        // The weights' sum from the packed fields, as the replacing path reads it back, so the two agree to the bit.
        windowWeight = sdfWindowWeight(weights);
        complete = ((mask == fullMask) && (weights != 0u));
        if (complete) {
            accepted = true;
            total = windowWeight;
            // A convex mean stays inside its samples' bounds. Preserve a constant component exactly even when the
            // division's approximate reciprocal rounds just below it before half-float storage (notably full coverage).
            accumulated = clamp((windowColor / windowWeight), windowMin, windowMax);
            accumulatedTransport = (windowTransport / windowWeight);
        }
    }
    if (!complete) {
        // The weighted mean of every sample the pixel has gathered: history clipped to this frame's neighbourhood, widened    } else {
        // The weighted mean of every sample the pixel has gathered: history clipped to this frame's neighbourhood, widened
        // to the feature the pixel remembers while its surface stands still, and weighed down by reactivity, then this
        // frame's samples, the transport with the color's weights. While less than one sample's full weight is gathered,
        // the spatial path shows through. A complete window shows its own mean, so only an incomplete one reads the
        // reprojected history.
        float historyWeight = 0.0;
        float4 history = float4(0.0, 0.0, 0.0, 0.0);
        float4 historyTransport = float4(0.0, 0.0, 0.0, 0.0);

        if (accepted) {
            float4 previous;
            float4 previousTransport;

            sdfHistoryAt(historyPosition, extent, previous, previousTransport);
            // Clipping infinity can produce NaN, and even zero history weight cannot remove it (NaN * 0 is NaN).
            accepted = (all(isfinite(previous)) && all(isfinite(previousTransport)));
            if (accepted) {
                float3 clipMin = (remembered ? min(boxMin, featureYCoCg) : boxMin);
                float3 clipMax = (remembered ? max(boxMax, featureYCoCg) : boxMax);

                historyWeight = (clamp(historyWeightGathered, 0.0, weightCap) * (1.0 - reactive));
                history = float4(
                    sdfFromYCoCg(sdfClipToBox(sdfToYCoCg(max(previous.rgb, 0.0)), clipMin, clipMax)),
                    clamp(previous.a, coverageMin, coverageMax)
                );
                historyTransport = clamp(previousTransport, transportMin, transportMax);
            }
        }

        total = (historyWeight + weightSum);
        accumulated = (((history * historyWeight) + sum) / max(total, 1.0e-6));
        accumulatedTransport = (((historyTransport * historyWeight) + transportSum) / max(total, 1.0e-6));
    }
    // The working history is half-float. A non-finite current sample contributes no reusable history, and a finite
    // reconstruction must remain representable when stored, so one bright transient cannot poison later frames.
    bool reusable = (all(isfinite(accumulated)) && all(isfinite(accumulatedTransport)) && isfinite(total));
    uint writtenWord = (reusable ? (mask | (weights << SDF_TEMPORAL_PERIOD)) : 0u);

    if (writtenWord != storedWord) {
        historyWindowRW[sdfHistoryWindowWord(id, 0u, extent)] = writtenWord;
    }
    // The feature the pixel carries on: this frame's sighting for a whole period, or the accepted history's with one
    // frame less, which the jitter comes round to before it runs out; a restarted history remembers none.
    float3 writtenFeature = (sighted ? sightedYCoCg : featureYCoCg);
    uint writtenLife = (sighted ? SDF_TEMPORAL_PERIOD : ((accepted && (featureLife != 0u)) ? (featureLife - 1u) : 0u));

    if (!reusable || !all(isfinite(writtenFeature))) {
        writtenLife = 0u;
    }
    historyColorRW[id.xy] = (reusable ? clamp(accumulated, -65504.0, 65504.0) : float4(0.0, 0.0, 0.0, 0.0));
    historySurfaceRW[sdfHistoryWord(pixelIndex, 0u, extent)] = centerIdentity;
    historySurfaceRW[sdfHistoryWord(pixelIndex, 1u, extent)] = (f32tof16(centerT) | (f32tof16(reusable ? min(total, weightCap) : 0.0) << 16u));
    uint2 historyTransportWords = sdfPackTransport(reusable ? accumulatedTransport : float4(0.0, 0.0, 0.0, 0.0));

    historySurfaceRW[sdfHistoryWord(pixelIndex, 2u, extent)] = historyTransportWords.x;
    historySurfaceRW[sdfHistoryWord(pixelIndex, 3u, extent)] = historyTransportWords.y;
    historySurfaceRW[sdfHistoryWord(pixelIndex, 4u, extent)] = (f32tof16(writtenFeature.x) | (f32tof16(writtenFeature.y) << 16u));
    // Use the same neighborhood and motion acceptance that reconstructed this pixel. A moving receiver
    // needs accumulated weight; a stationary one must have completed its jitter window.
    uint rate = 0u;
    if (passGroup.viewsRate.x > 1u && reusable && accepted && sampleValid && writtenLife == 0u &&
        coverageMin == 1.0 && (complete || (travel > SdfResolveFeatureTravel && historyWeightGathered >= 1.0))) {
        float tolerance = max(0.001, 0.06 * boxMax.x);
        bool smooth = all((boxMax - boxMin) <= tolerance);
        [unroll] for (uint tap = 0u; tap < 9u; tap++) {
            smooth = smooth && identities[tap] == centerIdentity;
        }
        if (smooth) {
            rate = (axisContrast.x <= 0.5 * tolerance ? 1u : 0u) | (axisContrast.y <= 0.5 * tolerance ? 2u : 0u);
        }
    }
    historySurfaceRW[sdfHistoryWord(pixelIndex, 5u, extent)] = (f32tof16(writtenFeature.z) | (writtenLife << 16u) | (rate << SdfHistoryRateShift));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint3 groupId : SV_GroupID, uint lane : SV_GroupIndex) {
    uint2 extent = passGroup.extent;
    uint2 render = passGroup.imageExtent;
    float2 jitter = passGroup.jitter;
    bool temporal = ((passGroup.temporal != 0u) && sdfDebugPreservesScene(passGroup.debugMode));

    // Every pixel but an exact copy of its own sample reads its footprint's samples from the group's tile; the choice is
    // the dispatch's, so the whole group reaches the tile's barrier.
    if (temporal || any(render != extent)) {
        sdfResolveTileFill((groupId.xy * 8u), lane, extent, render);
    }
    if (all(id.xy < extent)) {
        // The spatial path at this frame's sample grid: the sample of render pixel i lies at i + 0.5 + jitter.
        bool unjittered = (!temporal || all(jitter == 0.0));
        float3 airOrigin = passGroup.viewPosition;
        float3 airDirection = sdfResolveAirDirection(id.xy, extent);
        bool accepted = false;
        bool complete = false;
        float total = 0.0;
        float reactive = 0.0;
        float4 accumulated = float4(0.0, 0.0, 0.0, 0.0);
        float4 accumulatedTransport = float4(0.0, 0.0, 0.0, 0.0);
        bool gathered = false;
        float4 current = float4(0.0, 0.0, 0.0, 0.0);
        float4 currentTransport = float4(0.0, 0.0, 0.0, 0.0);

        if (temporal) {
            sdfResolveTemporal(id, extent, render, jitter, airDirection, accepted, complete, total, reactive, accumulated, accumulatedTransport, gathered, current, currentTransport);
        }

        // The spatial path shows where the history is rejected or holds less than one sample's weight, and by its
        // reactivity where the pixel is reactive; a pixel whose gathered weight covers it shows the mean alone and
        // reconstructs nothing. A reactive pixel gathers this frame's samples alone, whose weight swings about one sample's
        // as the jitter moves, so it takes the spatial path whatever it gathered (weighing the two by that weight would
        // switch a whole reactive screen between the two filters frame by frame), and it takes it on the unjittered grid, as
        // a view with no temporal path does: a screen reads its image along the unjittered ray, so that sample holds still.
        // Reactivity is the pixel's own sample's, so a jittered edge beside a screen keeps its history.
        float shown = (accepted ? ((complete ? 1.0 : saturate(total)) * (1.0 - reactive)) : 0.0);
        bool still = (unjittered || (reactive > 0.0));
        float4 resolved = accumulated;
        uint2 transportWord = sdfPackTransport(accumulatedTransport);

        if (shown < 1.0) {
            // A steady pixel whose history is rejected or still gathering shows this frame's gathered samples, the mean
            // its history restarts from, which costs it nothing more; only a reactive pixel, and a view with no temporal
            // path, reconstructs the grid.
            float4 spatial = current;
            float4 spatialTransport = currentTransport;
            uint2 spatialWord = sdfPackTransport(currentTransport);

            if (!gathered || (reactive > 0.0)) {
                sdfResolveSpatial(id.xy, (((((float2(id.xy) + 0.5) * float2(render)) / float2(extent)) - 0.5) - (still ? float2(0.0, 0.0) : jitter)),
                    (still && all(render == extent)), render, airOrigin, airDirection, spatial, spatialTransport, spatialWord);
            }
            resolved = lerp(spatial, accumulated, shown);
            transportWord = ((shown > 0.0) ? sdfPackTransport(lerp(spatialTransport, accumulatedTransport, shown)) : spatialWord);
        }

        output[id.xy] = resolved;
        sdfWorkTexels = 1u;
        transportRW[((id.y * extent.x) + id.x)] = transportWord;
    }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
