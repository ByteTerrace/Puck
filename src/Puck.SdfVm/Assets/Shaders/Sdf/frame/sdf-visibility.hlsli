// The visibility record, the surface sample record the shading stages read: what each full-extent pixel of each viewport
// sees, written by the surface pass and shaded by views. This module owns its layout; every reader and writer goes
// through the typed load and store functions below and holds only a record address. KEEP IN SYNC with
// SdfWorldPackage.VisibilityRecordByteLength.
//
// A record is eight words:
// 0: the ray parameter t (Euclidean distance along the normalized camera ray).
// 1: the identity.
// 2: the material.
// 3: a mesh hit's triangle.
// 4: a mesh hit's draw's frame slot (SDF_TRANSFORM_SLOT_NONE for a static draw), which the material's layers are placed
//    in and its lanes read from.
// 5: the geometric normal as a 16-bit signed octahedral pair (a zero normal is its own sentinel).
// 6: curvature and ambient occlusion as halves.
// 7: K, four eight-bit stable-slot visibilities, 1 where no shadow was rendered. The surface pass writes it for every
//    pixel; views reads it only while shadows are on and K is nonzero.
// The identity and the material are exact; the packed fields round only presentation values.
//
// The identity names what the pixel sees: its kind (SDF_VISIBILITY_KIND_*) above SDF_VISIBILITY_KIND_SHIFT and its
// source in SDF_VISIBILITY_SOURCE_MASK, both generated from SdfVisibility, which a pick decodes with. A background pixel
// is identity 0, and a mesh hit's source is its draw.
//
// The surface pass writes the one buffer through sdfVisibilityRecordsRW. Views, resolve and composite use the read-only
// binding after the planned transition. One dispatch never aliases SRV reads with UAV writes to the same allocation.
#ifndef SDF_VISIBILITY_HLSLI
#define SDF_VISIBILITY_HLSLI

#include "../field/sdf-octahedral.hlsli"
#include "sdf-shadow-visibility.hlsli"
#include "../isa/sdf-isa.hlsli"

#if defined(SDF_SURFACE_PASS)
#define sdfVisibilityRecordBuffer sdfVisibilityRecordsRW
#define SDF_VISIBILITY_WRITABLE
#else
#define sdfVisibilityRecordBuffer sdfVisibilityRecords
#endif

static const uint SdfVisibilityWords = 8u;
static const uint SdfVisibilityTWord = 0u;
static const uint SdfVisibilityIdentityWord = 1u;
static const uint SdfVisibilityMaterialWord = 2u;
static const uint SdfVisibilityTriangleWord = 3u;
static const uint SdfVisibilityFrameSlotWord = 4u;
static const uint SdfVisibilityNormalWord = 5u;
static const uint SdfVisibilityShadingWord = 6u;
static const uint SdfVisibilityShadowWord = 7u;
static const float SdfVisibilityNormalScale = 32767.0;
static const uint SdfVisibilityZeroNormal = 0x80008000u;
// The largest finite half: curvature past it saturates rather than becoming infinite.
static const float SdfVisibilityHalfMax = 65504.0;

struct SdfVisibility {
    float t;
    uint identity;
    int material;
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
bool sdfVisibilityHit(SdfVisibility visibility) {
    return (sdfVisibilityKind(visibility.identity) != SDF_VISIBILITY_KIND_BACKGROUND);
}
// A mesh hit's draw's frame slot, which the surface pass writes; SDF_TRANSFORM_SLOT_NONE for a miss.
int sdfVisibilityFrameSlot(uint record, SdfVisibility visibility) {
    return (sdfVisibilityHit(visibility) ? asint(sdfVisibilityRecordBuffer[record + SdfVisibilityFrameSlotWord]) : SDF_TRANSFORM_SLOT_NONE);
}
float4 sdfFrameLanes(int frameSlot) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (frameSlot != SDF_TRANSFORM_SLOT_NONE) return sdfDynamicTransforms[3u * (uint)frameSlot + 2u];
#endif
    return float4(0.0, 0.0, 0.0, 0.0);
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
    SdfVisibility visibility;
    visibility.t = asfloat(sdfVisibilityRecordBuffer[record + SdfVisibilityTWord]);
    visibility.identity = sdfVisibilityRecordBuffer[record + SdfVisibilityIdentityWord];
    visibility.material = asint(sdfVisibilityRecordBuffer[record + SdfVisibilityMaterialWord]);
    return visibility;
}
// Four stable-slot visibilities share the K word.
float4 sdfLoadVisibilityShadows(uint record) {
    return sdfUnpackShadowVisibility(sdfVisibilityRecordBuffer[record + SdfVisibilityShadowWord]);
}

// The surface sample the shading stages read: a pixel's whole record, decoded, with its frame slot's lanes.
struct SdfSurfaceSample {
    float t;
    bool hit;
    int material;
    int frameSlot;
    uint meshDraw;
    uint meshTriangle;
    float4 lanes;
    float3 normal;
    float curvature;
    float ambient;
};
SdfSurfaceSample sdfLoadSurfaceSample(uint record) {
    SdfVisibility visibility = sdfLoadVisibility(record);
    uint shading = sdfVisibilityRecordBuffer[record + SdfVisibilityShadingWord];
    SdfSurfaceSample sample;
    sample.t = visibility.t;
    sample.hit = sdfVisibilityHit(visibility);
    sample.material = visibility.material;
    sample.frameSlot = sdfVisibilityFrameSlot(record, visibility);
    sample.meshDraw = (sample.hit ? sdfVisibilitySource(visibility.identity) : 0u);
    sample.meshTriangle = (sample.hit ? sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord] : 0u);
    sample.lanes = sdfFrameLanes(sample.frameSlot);
    sample.normal = sdfVisibilityUnpackNormal(sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord]);
    sample.curvature = f16tof32(shading & 0xFFFFu);
    sample.ambient = f16tof32(shading >> 16u);
    return sample;
}

#if defined(SDF_VISIBILITY_WRITABLE)
// The whole record of one pixel: a miss carries identity 0, the far distance, no normal, an occlusion of one and full
// shadow visibility. Writing it counts the pixel as a texel the pass wrote (sdfWorkTexels).
struct SdfVisibilityRecordValue {
    SdfVisibility visibility;
    uint triangleIndex;
    int frameSlot;
    float3 normal;
    float curvature;
    float ambient;
    float4 shadows;
};
void sdfStoreVisibilityRecord(uint record, SdfVisibilityRecordValue value) {
    sdfVisibilityRecordBuffer[record + SdfVisibilityTWord] = asuint(value.visibility.t);
    sdfVisibilityRecordBuffer[record + SdfVisibilityIdentityWord] = value.visibility.identity;
    sdfVisibilityRecordBuffer[record + SdfVisibilityMaterialWord] = asuint(value.visibility.material);
    sdfVisibilityRecordBuffer[record + SdfVisibilityTriangleWord] = value.triangleIndex;
    sdfVisibilityRecordBuffer[record + SdfVisibilityFrameSlotWord] = asuint(value.frameSlot);
    sdfVisibilityRecordBuffer[record + SdfVisibilityNormalWord] = sdfVisibilityPackNormal(value.normal);
    sdfVisibilityRecordBuffer[record + SdfVisibilityShadingWord] =
        ((f32tof16(clamp(value.curvature, -SdfVisibilityHalfMax, SdfVisibilityHalfMax)) & 0xFFFFu) | ((f32tof16(value.ambient) & 0xFFFFu) << 16u));
    sdfVisibilityRecordBuffer[record + SdfVisibilityShadowWord] = sdfPackShadowVisibility(value.shadows);
    sdfWorkTexels = 1u;
}
#endif
#endif
