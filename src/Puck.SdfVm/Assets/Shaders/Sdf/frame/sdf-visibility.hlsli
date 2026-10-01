// The visibility record, the surface sample record the shading stages read: what each full-extent pixel of each viewport
// sees, written by the primary, surface, ambient and shadow stages and shaded by views. This module owns its layout; every reader
// and writer goes through the typed load and store functions below and holds only a record address. KEEP IN SYNC with
// SdfWorldPackage.VisibilityRecordByteLength.
//
// A record is sixteen words in six rows:
// V (4 words): the ray parameter t (Euclidean distance along the normalized camera ray), the identity, the material,
//    and the march flags (steps in bits 0..7, the saturated query count in bits 8..30).
// C (3 words): the terminal field radius, the acceptance threshold, then the seam's blend weight as a 15-bit fraction
//    in bits 0..14 and its other material plus one in bits 15..31, so every material from -1 up is exact.
// L (4 words): the winning SDF shape's transform slot (SDF_TRANSFORM_SLOT_NONE for static geometry), or a mesh hit's
//    triangle, in the first word. The remaining words are reserved. Anonymous lanes are read exactly from the winning transform row.
// N (2 words): the geometric normal as a 16-bit signed octahedral pair (a zero normal is its own sentinel), and the
//    gradient magnitude.
// S (2 words): curvature and ambient occlusion as halves, then the surface flags in bits 0..7 and the surface and
//    ambient query count, saturated, in bits 8..31. Surface flag bit 0 marks an ordinary lit, non-screen surface, the
//    only kind the ambient pass occludes.
// K (1 word): the key light's soft-shadow visibility, 1 where no shadow was marched. The shadow stage writes it only on
//    a frame whose soft shadows are on and that has a shadow light, and views reads it only then.
// V and the identity in it are exact; the packed fields round only presentation values.
// Primary writes V, C and L for every active pixel, misses included; surface writes N and S; ambient updates S; shadow
// writes K and adds its queries to S. Views reads the whole record once, as one surface sample (SdfSurfaceSample).
//
// The identity names what the pixel sees: its kind (SDF_VISIBILITY_KIND_*) above SDF_VISIBILITY_KIND_SHIFT and its
// source in SDF_VISIBILITY_SOURCE_MASK, both generated from SdfVisibility, which a pick decodes with. A background pixel
// is identity 0. An SDF hit's source is the winning instance's program ordinal plus one, so source 0 is
// geometry outside any instance. A mesh hit's source is its draw.
//
// The views set binds the one buffer twice: primary, surface, ambient and shadow write it through
// sdfVisibilityRecordsRW; views only reads it, through sdfVisibilityRecords. Every function below reaches it through sdfVisibilityRecordBuffer, the
// binding its kernel uses.
#ifndef SDF_VISIBILITY_HLSLI
#define SDF_VISIBILITY_HLSLI

#include "../field/sdf-octahedral.hlsli"
#include "../isa/sdf-isa.hlsli"

#if defined(SDF_PRIMARY_PASS) || defined(SDF_SURFACE_PASS) || defined(SDF_AMBIENT_PASS) || defined(SDF_SHADOW_PASS)
#define sdfVisibilityRecordBuffer sdfVisibilityRecordsRW
#define SDF_VISIBILITY_WRITABLE
#else
#define sdfVisibilityRecordBuffer sdfVisibilityRecords
#endif

static const uint SdfVisibilityWords = 16u;
static const uint SdfVisibilityRowV = 0u;
static const uint SdfVisibilityRowC = 4u;
static const uint SdfVisibilityRowL = 7u;
static const uint SdfVisibilityRowN = 11u;
static const uint SdfVisibilityRowS = 13u;
static const uint SdfVisibilityRowK = 15u;
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
    return ((kind << SDF_VISIBILITY_KIND_SHIFT) | (source & SDF_VISIBILITY_SOURCE_MASK));
}
uint sdfVisibilityKind(uint identity) {
    return (identity >> SDF_VISIBILITY_KIND_SHIFT);
}
uint sdfVisibilitySource(uint identity) {
    return (identity & SDF_VISIBILITY_SOURCE_MASK);
}
// An SDF march's identity: background on a miss, else the winning instance ordinal (-1 outside every instance) plus one.
uint sdfVisibilitySdfIdentity(bool hit, int instanceIndex) {
    return (hit ? sdfVisibilityIdentity(SDF_VISIBILITY_KIND_SDF, (uint)(instanceIndex + 1)) : 0u);
}
bool sdfVisibilityHit(SdfVisibility visibility) {
    return (sdfVisibilityKind(visibility.identity) != SDF_VISIBILITY_KIND_BACKGROUND);
}
// The winning shape's transform slot can differ from its instance's conservative bound slot.
int sdfVisibilityFrameSlot(uint record, SdfVisibility visibility) {
    return (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_SDF)
        ? asint(sdfVisibilityRecordBuffer[record + SdfVisibilityRowL]) : SDF_TRANSFORM_SLOT_NONE;
}
float4 sdfFrameLanes(int frameSlot) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (frameSlot != SDF_TRANSFORM_SLOT_NONE) return sdfDynamicTransforms[3u * (uint)frameSlot + 2u];
#endif
    return float4(0.0, 0.0, 0.0, 0.0);
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
    return uint4(sdfVisibilityRecordBuffer[word], sdfVisibilityRecordBuffer[word + 1u], sdfVisibilityRecordBuffer[word + 2u], sdfVisibilityRecordBuffer[word + 3u]);
}
// The seam word: the blend weight's 15-bit fraction, then the other material plus one. Each field is masked to its
// bits, so a NaN weight cannot write into the material's.
uint sdfVisibilityPackBlend(float weight, int other) {
    return ((((uint)round(saturate(weight) * SdfVisibilityBlendScale)) & SdfVisibilityBlendMask) | (((uint)(other + 1)) << SdfVisibilityBlendOtherShift));
}
// A normal as a 16-bit signed octahedral pair, each half masked to its bits so a NaN component cannot write into the
// other; the zero normal a miss carries is a sentinel no unit normal encodes to.
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
    uint blend = sdfVisibilityRecordBuffer[word + 2u];
    SdfVisibilityCoverage coverage;
    coverage.terminalRadius = asfloat(sdfVisibilityRecordBuffer[word]);
    coverage.threshold = asfloat(sdfVisibilityRecordBuffer[word + 1u]);
    coverage.blendWeight = (float(blend & SdfVisibilityBlendMask) / SdfVisibilityBlendScale);
    coverage.blendOther = (((int)(blend >> SdfVisibilityBlendOtherShift)) - 1);
    return coverage;
}
SdfVisibilityNormal sdfLoadVisibilityNormal(uint record) {
    uint word = (record + SdfVisibilityRowN);
    SdfVisibilityNormal normal;
    normal.normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[word]);
    normal.gradientMagnitude = asfloat(sdfVisibilityRecordBuffer[word + 1u]);
    return normal;
}
// The key light's soft-shadow visibility, which the K row carries.
float sdfLoadVisibilityKey(uint record) {
    return asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityRowK]);
}
// A mesh hit's triangle, which its L row carries.
uint sdfVisibilityMeshTriangle(uint record) {
    return sdfVisibilityRecordBuffer[record + SdfVisibilityRowL];
}
SdfVisibilitySurface sdfLoadVisibilitySurface(uint record) {
    uint word = (record + SdfVisibilityRowS);
    uint shading = sdfVisibilityRecordBuffer[word];
    uint counts = sdfVisibilityRecordBuffer[word + 1u];
    SdfVisibilitySurface surface;
    surface.curvature = f16tof32(shading & 0xFFFFu);
    surface.ambient = f16tof32(shading >> 16u);
    surface.flags = (counts & SdfVisibilitySurfaceFlagMask);
    surface.queries = float(counts >> SdfVisibilitySurfaceQueryShift);
    return surface;
}

// The surface sample the shading stages read: a pixel's whole record, decoded. A mesh hit carries its draw in its
// identity and its triangle in the L row, so its lanes read zero and its frame slot SDF_TRANSFORM_SLOT_NONE.
struct SdfSurfaceSample {
    float t;
    bool hit;
    bool mesh;
    int material;
    int frameSlot;
    uint meshDraw;
    uint meshTriangle;
    // The primary march's steps and the queries every march before the surface stage made.
    uint steps;
    float queries;
    float4 lanes;
    float terminalRadius;
    float threshold;
    float blendWeight;
    int blendOther;
    float3 normal;
    float gradientMagnitude;
    float curvature;
    float ambient;
    // The queries the surface, ambient and shadow stages made, and the surface flags.
    float surfaceQueries;
    uint surfaceFlags;
    // The key light's soft-shadow visibility, current only on a frame whose soft shadows are on and that has a shadow
    // light.
    float keyVisibility;
};
SdfSurfaceSample sdfLoadSurfaceSample(uint record) {
    SdfVisibility visibility = sdfLoadVisibility(record);
    SdfVisibilityCoverage coverage = sdfLoadVisibilityCoverage(record);
    SdfVisibilityNormal normal = sdfLoadVisibilityNormal(record);
    SdfVisibilitySurface surface = sdfLoadVisibilitySurface(record);
    SdfSurfaceSample sample;
    sample.t = visibility.t;
    sample.hit = sdfVisibilityHit(visibility);
    sample.mesh = (sdfVisibilityKind(visibility.identity) == SDF_VISIBILITY_KIND_MESH);
    sample.material = visibility.material;
    sample.frameSlot = sdfVisibilityFrameSlot(record, visibility);
    sample.meshDraw = (sample.mesh ? sdfVisibilitySource(visibility.identity) : 0u);
    sample.meshTriangle = (sample.mesh ? sdfVisibilityMeshTriangle(record) : 0u);
    sample.steps = sdfVisibilitySteps(visibility);
    sample.queries = (float)sdfVisibilityQueries(visibility);
    sample.lanes = sdfFrameLanes(sample.frameSlot);
    sample.terminalRadius = coverage.terminalRadius;
    sample.threshold = coverage.threshold;
    sample.blendWeight = coverage.blendWeight;
    sample.blendOther = coverage.blendOther;
    sample.normal = normal.normal;
    sample.gradientMagnitude = normal.gradientMagnitude;
    sample.curvature = surface.curvature;
    sample.ambient = surface.ambient;
    sample.surfaceQueries = surface.queries;
    sample.surfaceFlags = surface.flags;
    sample.keyVisibility = sdfLoadVisibilityKey(record);
    return sample;
}

#ifdef SDF_VISIBILITY_WRITABLE
// Writes one word of the pixel's record: every store below goes through it, so a pass that writes any of a pixel's record
// counts the pixel as a texel it wrote (sdfWorkTexels).
void sdfVisibilityStoreWord(uint word, uint value) {
    sdfVisibilityRecordBuffer[word] = value;
    sdfWorkTexels = 1u;
}
void sdfVisibilityStoreRow(uint word, uint4 bits) {
    sdfVisibilityStoreWord(word, bits.x);
    sdfVisibilityStoreWord(word + 1u, bits.y);
    sdfVisibilityStoreWord(word + 2u, bits.z);
    sdfVisibilityStoreWord(word + 3u, bits.w);
}
void sdfStoreVisibility(uint record, SdfVisibility visibility) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowV, uint4(asuint(visibility.t), visibility.identity, asuint(visibility.material), visibility.flags));
}
void sdfStoreVisibilityCoverage(uint record, SdfVisibilityCoverage coverage) {
    uint word = (record + SdfVisibilityRowC);
    sdfVisibilityStoreWord(word, asuint(coverage.terminalRadius));
    sdfVisibilityStoreWord(word + 1u, asuint(coverage.threshold));
    sdfVisibilityStoreWord(word + 2u, sdfVisibilityPackBlend(coverage.blendWeight, coverage.blendOther));
}
void sdfStoreVisibilityFrameSlot(uint record, int frameSlot) {
    sdfVisibilityStoreWord(record + SdfVisibilityRowL, asuint(frameSlot));
}
void sdfStoreVisibilityKey(uint record, float visibility) {
    sdfVisibilityStoreWord(record + SdfVisibilityRowK, asuint(visibility));
}
void sdfStoreVisibilityMeshTriangle(uint record, uint triangleIndex) {
    sdfVisibilityStoreRow(record + SdfVisibilityRowL, uint4(triangleIndex, 0u, 0u, 0u));
}
void sdfStoreVisibilityNormal(uint record, SdfVisibilityNormal normal) {
    uint word = (record + SdfVisibilityRowN);
    sdfVisibilityStoreWord(word, sdfVisibilityPackNormal(normal.normal));
    sdfVisibilityStoreWord(word + 1u, asuint(normal.gradientMagnitude));
}
void sdfStoreVisibilitySurface(uint record, SdfVisibilitySurface surface) {
    uint word = (record + SdfVisibilityRowS);
    sdfVisibilityStoreWord(word, ((f32tof16(clamp(surface.curvature, -SdfVisibilityHalfMax, SdfVisibilityHalfMax)) & 0xFFFFu) | ((f32tof16(surface.ambient) & 0xFFFFu) << 16u)));
    sdfVisibilityStoreWord(word + 1u, ((surface.flags & SdfVisibilitySurfaceFlagMask) | (min((uint)surface.queries, SdfVisibilitySurfaceQueryMask) << SdfVisibilitySurfaceQueryShift)));
}
#endif
#endif
