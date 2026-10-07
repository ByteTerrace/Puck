// A view's surface transport: what the air between the camera and each render sample's surface does to it, carried from
// the sample to the composite with the weights its color is (SdfSurfaceTransport is the CPU reference). Views multiplies
// each hit's lit color by the atmosphere's transmittance (sdf-atmosphere.hlsli), so the lit image is premultiplied by
// coverage and transmittance alike. The transport is four numbers a sample, each linear in the premultiplied space the
// resolve filters and the history accumulates: each atmosphere kind's in-scatter weight, coverage times its SdfAir weight,
// which the composite scales the kind's in-scatter colour by, and the coverage over the ray distance, from which the
// composite takes the surface share's depth to clip the bounded media at. A kind's in-scatter colour is a function of the
// pixel's direction alone, so a filtered weight is exactly the samples' weighted in-scatter. Every pass that reads them
// reads the sky interface's atmosphere lanes (sdfSky).
#ifndef SHADE_SDF_TRANSPORT_HLSLI
#define SHADE_SDF_TRANSPORT_HLSLI
#include "sdf-atmosphere.hlsli"

// The scale the coverage over the ray distance is held at, so a half float keeps it normal over every distance a march
// reaches: from SDF_MINIMUM_NEAR (1024 / 0.02 = 51200, below the half's 65504) to the far limit 8192 at a coverage of
// one thousandth (1.25e-4, above its smallest normal 6.1e-5), each within a relative 2^-11. KEEP IN SYNC with
// SdfSurfaceTransport.InverseDistanceScale.
static const float SdfTransportInverseDistanceScale = 1024.0;

// The fraction of a surface's light the atmosphere lets through over a ray distance from the eye at `origin` along the
// unit `direction`. The transport is `precise` (sdf-atmosphere.hlsli), so no compiler contracts or reorders it: every
// site that evaluates it on the same inputs, in any kernel, gets the same bits, which a resolved copy of a sample relies
// on to composite as its native view does.
float sdfAirTransmittance(float3 origin, float3 direction, float t) {
    return sdfAirAlong(origin, direction, t, true).transmittance;
}
// One render sample's transport from its coverage and its surface's ray distance, zero for a sample with no surface: the
// fog's in-scatter weight in x, the scaled coverage over the distance in y, the haze's in z and the medium's in w.
float4 sdfSampleTransport(float coverage, float t, float3 origin, float3 direction) {
    if ((coverage <= 0.0) || (t <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    SdfAir air = sdfAirAlong(origin, direction, t, true);
    precise float fog = (coverage * air.weights.x);
    precise float haze = (coverage * air.weights.y);
    precise float medium = (coverage * air.weights.z);
    precise float inverseDistance = ((coverage * SdfTransportInverseDistanceScale) / t);

    return float4(fog, inverseDistance, haze, medium);
}
// The ray distance the surface share of a pixel is clipped at: its coverage over its resolved coverage over the
// distance, the harmonic mean of its samples' distances, each weighted as its color is. Zero for a pixel with no surface.
float sdfTransportDistance(float coverage, float4 transport) {
    return (((coverage > 0.0) && (transport.y > 0.0)) ? ((coverage * SdfTransportInverseDistanceScale) / transport.y) : 0.0);
}
// A transport is two words, of one of two kinds, told apart by the top bit of the first. Clear: a reconstruction's
// transport, four half floats, the fog's weight and the scaled inverse distance in the first word (the inverse distance's
// sign bit masked so it never sets the top bit), the haze's and the medium's weights in the second. Set: one render sample
// the resolve copied whole, its ray distance's float bits below the top bit (zero for a sample with no surface) and a
// zero second word, from which the composite derives the transport with the arithmetic a native view's composite runs on
// the same sample.
static const uint SdfTransportSampleBit = 0x80000000u;

uint2 sdfPackTransport(float4 transport) {
    return uint2(
        (f32tof16(transport.x) | ((f32tof16(min(transport.y, 65504.0)) & 0x7FFFu) << 16u)),
        (f32tof16(transport.z) | (f32tof16(transport.w) << 16u))
    );
}
uint2 sdfTransportSampleWord(float t) {
    return uint2(((asuint(t) & ~SdfTransportSampleBit) | SdfTransportSampleBit), 0u);
}
bool sdfTransportIsSample(uint2 word) {
    return ((word.x & SdfTransportSampleBit) != 0u);
}
float sdfTransportSampleDistance(uint2 word) {
    return asfloat(word.x & ~SdfTransportSampleBit);
}
float4 sdfUnpackTransport(uint2 packed) {
    return float4(f16tof32(packed.x & 0xFFFFu), f16tof32(packed.x >> 16u), f16tof32(packed.y & 0xFFFFu), f16tof32(packed.y >> 16u));
}
#endif
