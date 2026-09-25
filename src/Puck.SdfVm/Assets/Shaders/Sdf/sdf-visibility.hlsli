// The visibility record: what each full-extent pixel of each viewport sees, written by the hit passes and shaded by
// views. This module owns its layout; every reader and writer goes through the typed load and store functions below
// and holds only a record address. KEEP IN SYNC with SdfWorldEngine.PrimaryHitByteLength, PrimaryHitBindingIndex,
// PrimaryHitReadBindingIndex and their places in the views binding order.
//
// A record is fifteen words in five rows:
// V (4 words): the ray parameter t (Euclidean distance along the normalized camera ray), the identity, the material,
//    and the march flags (steps in bits 0..7, the saturated query count in bits 8..30).
// C (3 words): the terminal field radius, the acceptance threshold, then the seam's blend weight as a 15-bit fraction
//    in bits 0..14 and its other material plus one in bits 15..31, so every material from -1 up is exact.
// L (4 words): the winning instance's four anonymous lanes, as authored floats.
// N (2 words): the geometric normal as a 16-bit signed octahedral pair (a zero normal is its own sentinel), and the
//    gradient magnitude.
// S (2 words): curvature and ambient occlusion as halves, then the surface flags in bits 0..7 and the surface and
//    ambient query count, saturated, in bits 8..31. Surface flag bit 0 marks an ordinary lit, non-screen surface, the
//    only kind the ambient pass occludes.
// V and the identity in it are exact; the packed fields round only presentation values.
// Primary writes V, C and L for every active pixel, misses included; surface writes N and S; ambient updates S.
//
// The identity names what the pixel sees: its kind in bits 31..30 and its source in bits 29..0. A background pixel
// is identity 0. An SDF hit's source is the winning instance's dynamic-transform frame slot plus one, so source 0 is
// the static field. A mesh hit's source is its draw.
//
// The views layout binds the one buffer twice: primary, surface and ambient write it through the read-write binding
// 49 (u5, after the five source images u0..u4); views only reads it, through the read-only binding 50 (t45, after
// the cull buffer t44). Scalar uint storage matches the engine's four-byte descriptor stride on both backends.
#ifndef SDF_VISIBILITY_HLSLI
#define SDF_VISIBILITY_HLSLI

#include "sdf-octahedral.hlsli"

#if defined(SDF_PRIMARY_PASS) || defined(SDF_SURFACE_PASS) || defined(SDF_AMBIENT_PASS)
[[vk::binding(49, 0)]] RWStructuredBuffer<uint> sdfVisibilityRecords : register(u5);
#define SDF_VISIBILITY_WRITABLE
#else
[[vk::binding(50, 0)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t45);
#endif

static const uint SdfVisibilityWords = 15u;
static const uint SdfVisibilityRowV = 0u;
static const uint SdfVisibilityRowC = 4u;
static const uint SdfVisibilityRowL = 7u;
static const uint SdfVisibilityRowN = 11u;
static const uint SdfVisibilityRowS = 13u;
static const float SdfVisibilityBlendScale = 32767.0;
static const uint SdfVisibilityBlendMask = 0x7FFFu;
static const uint SdfVisibilityBlendOtherShift = 15u;
static const float SdfVisibilityNormalScale = 32767.0;
static const uint SdfVisibilityZeroNormal = 0x80008000u;
static const uint SdfVisibilitySurfaceFlagMask = 255u;
static const uint SdfVisibilitySurfaceQueryShift = 8u;
static const uint SdfVisibilitySurfaceQueryMask = 0xFFFFFFu;
// The largest finite half: curvature past it saturates rather than becoming infinite.
static const float SdfVisibilityHalfMax = 65504.0;

static const uint SdfVisibilityKindBackground = 0u;
static const uint SdfVisibilityKindSdf = 1u;
static const uint SdfVisibilityKindMesh = 2u;
static const uint SdfVisibilityKindShift = 30u;
static const uint SdfVisibilitySourceMask = 0x3FFFFFFFu;
static const uint SdfVisibilityStepMask = 255u;
static const uint SdfVisibilityQueryShift = 8u;
static const uint SdfVisibilityQueryMask = 0x7FFFFFu;

struct SdfVisibility {
    float t;
    uint identity;
    int material;
    uint flags;
};
struct SdfVisibilityCoverage {
    float terminalRadius;
    float threshold;
    float blendWeight;
    int blendOther;
};
struct SdfVisibilityNormal {
    float3 normal;
    float gradientMagnitude;
};
struct SdfVisibilitySurface {
    float curvature;
    float queries;
    float ambient;
    uint flags;
};

// The record of `pixel` in viewport `viewIndex`; `extent` is the full output extent every viewport slice spans.
uint sdfVisibilityRecord(uint2 pixel, uint viewIndex, uint2 extent) {
    return (SdfVisibilityWords * (((viewIndex * extent.y) + pixel.y) * extent.x + pixel.x));
}

uint sdfVisibilityIdentity(uint kind, uint source) {
    return ((kind << SdfVisibilityKindShift) | (source & SdfVisibilitySourceMask));
}
uint sdfVisibilityKind(uint identity) {
    return (identity >> SdfVisibilityKindShift);
}
uint sdfVisibilitySource(uint identity) {
    return (identity & SdfVisibilitySourceMask);
}
// An SDF march's identity: background on a miss, else the winning frame slot (-1 for the static field) plus one.
uint sdfVisibilitySdfIdentity(bool hit, int frameSlot) {
    return (hit ? sdfVisibilityIdentity(SdfVisibilityKindSdf, (uint)(frameSlot + 1)) : 0u);
}
bool sdfVisibilityHit(SdfVisibility visibility) {
    return (sdfVisibilityKind(visibility.identity) != SdfVisibilityKindBackground);
}
// The dynamic-transform frame slot of an SDF hit; -1 for the static field and for every other kind.
int sdfVisibilityFrameSlot(SdfVisibility visibility) {
    return ((sdfVisibilityKind(visibility.identity) == SdfVisibilityKindSdf) ? (((int)sdfVisibilitySource(visibility.identity)) - 1) : -1);
}
// Selected march steps saturate at 255; total queries across all marches saturate at 2^23 - 1.
uint sdfVisibilityFlags(int steps, float queries) {
    return (min((uint)steps, SdfVisibilityStepMask) | (min((uint)queries, SdfVisibilityQueryMask) << SdfVisibilityQueryShift));
}
uint sdfVisibilitySteps(SdfVisibility visibility) {
    return (visibility.flags & SdfVisibilityStepMask);
}
uint sdfVisibilityQueries(SdfVisibility visibility) {
    return ((visibility.flags >> SdfVisibilityQueryShift) & SdfVisibilityQueryMask);
}

uint4 sdfVisibilityLoadRow(uint word) {
    return uint4(sdfVisibilityRecords[word], sdfVisibilityRecords[word + 1u], sdfVisibilityRecords[word + 2u], sdfVisibilityRecords[word + 3u]);
}
// The seam word: the blend weight's 15-bit fraction, then the other material plus one.
uint sdfVisibilityPackBlend(float weight, int other) {
    return (((uint)round(saturate(weight) * SdfVisibilityBlendScale)) | (((uint)(other + 1)) << SdfVisibilityBlendOtherShift));
}
// A normal as a 16-bit signed octahedral pair; the zero normal a miss carries is a sentinel no unit normal encodes to.
uint sdfVisibilityPackNormal(float3 normal) {
    if (dot(normal, normal) == 0.0) {
        return SdfVisibilityZeroNormal;
    }

    int2 q = (int2)round(clamp(sdfOctEncode(normal), -1.0, 1.0) * SdfVisibilityNormalScale);

    return (((uint)q.x & 0xFFFFu) | (((uint)q.y & 0xFFFFu) << 16u));
}
float3 sdfVisibilityUnpackNormal(uint packed) {
    if (packed == SdfVisibilityZeroNormal) {
        return float3(0.0, 0.0, 0.0);
    }

    int2 q = int2((((int)(packed << 16u)) >> 16), (((int)packed) >> 16));

    return sdfOctDecode(float2(q) / SdfVisibilityNormalScale);
}
SdfVisibility sdfLoadVisibility(uint record) {
    uint4 row = sdfVisibilityLoadRow(record + SdfVisibilityRowV);
    SdfVisibility visibility;
    visibility.t = asfloat(row.x);
    visibility.identity = row.y;
    visibility.material = asint(row.z);
    visibility.flags = row.w;
    return visibility;
}
SdfVisibilityCoverage sdfLoadVisibilityCoverage(uint record) {
    uint word = (record + SdfVisibilityRowC);
    uint blend = sdfVisibilityRecords[word + 2u];
    SdfVisibilityCoverage coverage;
    coverage.terminalRadius = asfloat(sdfVisibilityRecords[word]);
    coverage.threshold = asfloat(sdfVisibilityRecords[word + 1u]);
    coverage.blendWeight = (float(blend & SdfVisibilityBlendMask) / SdfVisibilityBlendScale);
    coverage.blendOther = (((int)(blend >> SdfVisibilityBlendOtherShift)) - 1);
    return coverage;
}
float4 sdfLoadVisibilityLanes(uint record) {
    return asfloat(sdfVisibilityLoadRow(record + SdfVisibilityRowL));
}
SdfVisibilityNormal sdfLoadVisibilityNormal(uint record) {
    uint word = (record + SdfVisibilityRowN);
    SdfVisibilityNormal normal;
    normal.normal = sdfVisibilityUnpackNormal(sdfVisibilityRecords[word]);
    normal.gradientMagnitude = asfloat(sdfVisibilityRecords[word + 1u]);
    return normal;
}
SdfVisibilitySurface sdfLoadVisibilitySurface(uint record) {
    uint word = (record + SdfVisibilityRowS);
    uint shading = sdfVisibilityRecords[word];
    uint counts = sdfVisibilityRecords[word + 1u];
    SdfVisibilitySurface surface;
    surface.curvature = f16tof32(shading & 0xFFFFu);
    surface.ambient = f16tof32(shading >> 16u);
    surface.flags = (counts & SdfVisibilitySurfaceFlagMask);
    surface.queries = float(counts >> SdfVisibilitySurfaceQueryShift);
    return surface;
}

#ifdef SDF_VISIBILITY_WRITABLE
void sdfVisibilityStoreRow(uint word, uint4 bits) {
    sdfVisibilityRecords[word] = bits.x;
    sdfVisibilityRecords[word + 1u] = bits.y;
    sdfVisibilityRecords[word + 2u] = bits.z;
    sdfVisibilityRecords[word + 3u] = bits.w;
}
void sdfStoreVisibility(uint record, SdfVisibility visibility) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowV, uint4(asuint(visibility.t), visibility.identity, asuint(visibility.material), visibility.flags));
}
void sdfStoreVisibilityCoverage(uint record, SdfVisibilityCoverage coverage) {
    uint word = (record + SdfVisibilityRowC);
    sdfVisibilityRecords[word] = asuint(coverage.terminalRadius);
    sdfVisibilityRecords[word + 1u] = asuint(coverage.threshold);
    sdfVisibilityRecords[word + 2u] = sdfVisibilityPackBlend(coverage.blendWeight, coverage.blendOther);
}
void sdfStoreVisibilityLanes(uint record, float4 lanes) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowL, asuint(lanes));
}
void sdfStoreVisibilityNormal(uint record, SdfVisibilityNormal normal) {
    uint word = (record + SdfVisibilityRowN);
    sdfVisibilityRecords[word] = sdfVisibilityPackNormal(normal.normal);
    sdfVisibilityRecords[word + 1u] = asuint(normal.gradientMagnitude);
}
void sdfStoreVisibilitySurface(uint record, SdfVisibilitySurface surface) {
    uint word = (record + SdfVisibilityRowS);
    sdfVisibilityRecords[word] = (f32tof16(clamp(surface.curvature, -SdfVisibilityHalfMax, SdfVisibilityHalfMax)) | (f32tof16(surface.ambient) << 16u));
    sdfVisibilityRecords[word + 1u] = ((surface.flags & SdfVisibilitySurfaceFlagMask) | (min((uint)surface.queries, SdfVisibilitySurfaceQueryMask) << SdfVisibilitySurfaceQueryShift));
}
#endif
#endif
