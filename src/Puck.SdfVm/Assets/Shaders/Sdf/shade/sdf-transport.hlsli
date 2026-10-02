// A view's surface transport: what the air between the camera and each render sample's surface does to it, carried from
// the sample to the composite with the weights its color is (SdfSurfaceTransport is the CPU reference). Views multiplies
// each hit's lit color by its fog transmittance, so the lit image is premultiplied by coverage and transmittance alike.
// The transport is two numbers a sample, each linear in the premultiplied space the resolve filters and the history
// accumulates: the fog's in-scatter weight, coverage times one minus the transmittance, which the composite scales the
// sky's gradient by, and the coverage over the ray distance, from which the composite takes the surface share's depth to
// clip the bounded media at. Every pass that reads them reads the sky interface's fog density (sdfSky).
#ifndef SHADE_SDF_TRANSPORT_HLSLI
#define SHADE_SDF_TRANSPORT_HLSLI

// The scale the coverage over the ray distance is held at, so a half float keeps it normal over every distance a march
// reaches: from SDF_MINIMUM_NEAR (1024 / 0.02 = 51200, below the half's 65504) to the far limit 8192 at a coverage of
// one thousandth (1.25e-4, above its smallest normal 6.1e-5), each within a relative 2^-11. KEEP IN SYNC with
// SdfSurfaceTransport.InverseDistanceScale.
static const float SdfTransportInverseDistanceScale = 1024.0;

// The fraction of a surface's light the fog lets through over a ray distance: exp(-density * t), one without fog. The
// transport below is `precise`, so no compiler contracts or reorders it: every site that evaluates it on the same inputs,
// in any kernel, gets the same bits, which a resolved copy of a sample relies on to composite as its native view does.
float sdfFogTransmittance(float t) {
    float density = sdfSky[0].FogDensity;
    precise float transmittance = ((density > 0.0) ? exp(-density * t) : 1.0);

    return transmittance;
}
// One render sample's transport from its coverage and its surface's ray distance, zero for a sample with no surface.
float2 sdfSampleTransport(float coverage, float t) {
    if ((coverage <= 0.0) || (t <= 0.0)) {
        return float2(0.0, 0.0);
    }

    precise float fog = (coverage * (1.0 - sdfFogTransmittance(t)));
    precise float inverseDistance = ((coverage * SdfTransportInverseDistanceScale) / t);

    return float2(fog, inverseDistance);
}
// The ray distance the surface share of a pixel is clipped at: its coverage over its resolved coverage over the
// distance, the harmonic mean of its samples' distances, each weighted as its color is. Zero for a pixel with no surface.
float sdfTransportDistance(float coverage, float2 transport) {
    return (((coverage > 0.0) && (transport.y > 0.0)) ? ((coverage * SdfTransportInverseDistanceScale) / transport.y) : 0.0);
}
// A transport word is one of two kinds, told apart by its top bit. Clear: a reconstruction's transport, two half floats,
// the in-scatter weight low and the scaled inverse distance high, the high half's sign bit masked so it never sets the
// top bit. Set: one render sample the resolve copied whole, its ray distance's float bits below the top bit (zero for a
// sample with no surface), from which the composite derives the transport with the arithmetic a native view's composite
// runs on the same sample.
static const uint SdfTransportSampleBit = 0x80000000u;

uint sdfPackTransport(float2 transport) {
    return (f32tof16(transport.x) | ((f32tof16(min(transport.y, 65504.0)) & 0x7FFFu) << 16u));
}
uint sdfTransportSampleWord(float t) {
    return ((asuint(t) & ~SdfTransportSampleBit) | SdfTransportSampleBit);
}
bool sdfTransportIsSample(uint word) {
    return ((word & SdfTransportSampleBit) != 0u);
}
float sdfTransportSampleDistance(uint word) {
    return asfloat(word & ~SdfTransportSampleBit);
}
float2 sdfUnpackTransport(uint packed) {
    return float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16u));
}
#endif
