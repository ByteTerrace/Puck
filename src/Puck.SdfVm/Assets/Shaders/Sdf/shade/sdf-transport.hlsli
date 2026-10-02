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

// The fraction of a surface's light the fog lets through over a ray distance: exp(-density * t), one without fog.
float sdfFogTransmittance(float t) {
    float density = sdfSky[0].FogDensity;

    return ((density > 0.0) ? exp(-density * t) : 1.0);
}
// One render sample's transport from its coverage and its surface's ray distance, zero for a sample with no surface.
float2 sdfSampleTransport(float coverage, float t) {
    if ((coverage <= 0.0) || (t <= 0.0)) {
        return float2(0.0, 0.0);
    }

    return float2((coverage * (1.0 - sdfFogTransmittance(t))), ((coverage * SdfTransportInverseDistanceScale) / t));
}
// The ray distance the surface share of a pixel is clipped at: its coverage over its resolved coverage over the
// distance, the harmonic mean of its samples' distances, each weighted as its color is. Zero for a pixel with no surface.
float sdfTransportDistance(float coverage, float2 transport) {
    return (((coverage > 0.0) && (transport.y > 0.0)) ? ((coverage * SdfTransportInverseDistanceScale) / transport.y) : 0.0);
}
// The transport as two half floats in one word: the in-scatter weight low, the scaled inverse distance high.
uint sdfPackTransport(float2 transport) {
    return (f32tof16(transport.x) | (f32tof16(min(transport.y, 65504.0)) << 16u));
}
float2 sdfUnpackTransport(uint packed) {
    return float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16u));
}
#endif
