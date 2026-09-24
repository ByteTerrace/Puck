// The visibility record: what each full-extent pixel of each viewport sees, written by the hit passes and shaded by
// views. This module owns its layout; every reader and writer goes through the typed load and store functions below
// and holds only a record address. KEEP IN SYNC with SdfWorldEngine.PrimaryHitByteLength, PrimaryHitBindingIndex,
// PrimaryHitReadBindingIndex and their places in the views binding order.
//
// A record is five 16-byte rows (twenty words):
// V: the ray parameter t (Euclidean distance along the normalized camera ray), the identity, the material, and the
//    march flags (steps in bits 0..7, the saturated query count in bits 8..30).
// C: the terminal field radius, the acceptance threshold, the seam blend weight, and the seam's other material.
// L: the winning instance's four anonymous lanes.
// N: the geometric normal and the gradient magnitude.
// S: curvature, the surface and ambient query count, ambient occlusion, and the surface flags. Surface flag bit 0
//    marks an ordinary lit, non-screen surface, the only kind the ambient pass occludes.
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

#if defined(SDF_PRIMARY_PASS) || defined(SDF_SURFACE_PASS) || defined(SDF_AMBIENT_PASS)
[[vk::binding(49, 0)]] RWStructuredBuffer<uint> sdfVisibilityRecords : register(u5);
#define SDF_VISIBILITY_WRITABLE
#else
[[vk::binding(50, 0)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t45);
#endif

static const uint SdfVisibilityWords = 20u;
static const uint SdfVisibilityRowV = 0u;
static const uint SdfVisibilityRowC = 4u;
static const uint SdfVisibilityRowL = 8u;
static const uint SdfVisibilityRowN = 12u;
static const uint SdfVisibilityRowS = 16u;

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
    uint4 row = sdfVisibilityLoadRow(record + SdfVisibilityRowC);
    SdfVisibilityCoverage coverage;
    coverage.terminalRadius = asfloat(row.x);
    coverage.threshold = asfloat(row.y);
    coverage.blendWeight = asfloat(row.z);
    coverage.blendOther = asint(row.w);
    return coverage;
}
float4 sdfLoadVisibilityLanes(uint record) {
    return asfloat(sdfVisibilityLoadRow(record + SdfVisibilityRowL));
}
SdfVisibilityNormal sdfLoadVisibilityNormal(uint record) {
    float4 row = asfloat(sdfVisibilityLoadRow(record + SdfVisibilityRowN));
    SdfVisibilityNormal normal;
    normal.normal = row.xyz;
    normal.gradientMagnitude = row.w;
    return normal;
}
SdfVisibilitySurface sdfLoadVisibilitySurface(uint record) {
    uint4 row = sdfVisibilityLoadRow(record + SdfVisibilityRowS);
    SdfVisibilitySurface surface;
    surface.curvature = asfloat(row.x);
    surface.queries = asfloat(row.y);
    surface.ambient = asfloat(row.z);
    surface.flags = row.w;
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
    sdfVisibilityStoreRow(record + SdfVisibilityRowC, uint4(asuint(coverage.terminalRadius), asuint(coverage.threshold), asuint(coverage.blendWeight), asuint(coverage.blendOther)));
}
void sdfStoreVisibilityLanes(uint record, float4 lanes) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowL, asuint(lanes));
}
void sdfStoreVisibilityNormal(uint record, SdfVisibilityNormal normal) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowN, asuint(float4(normal.normal, normal.gradientMagnitude)));
}
void sdfStoreVisibilitySurface(uint record, SdfVisibilitySurface surface) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowS, uint4(asuint(surface.curvature), asuint(surface.queries), asuint(surface.ambient), surface.flags));
}
#endif

#endif
