// Octahedral encoding of a unit direction into [-1, 1]^2 (Meyer et al., "On Floating-Point Normal Vectors"). The star
// field uses it as its cell-grid domain, and the visibility record stores its geometric normal through it. No texture,
// no per-pixel trig; area-preserving enough for a uniform-reading star density across the sky.
#ifndef SDF_OCTAHEDRAL_HLSLI
#define SDF_OCTAHEDRAL_HLSLI

float2 sdfOctEncode(float3 n) {
    float2 p = (n.xy * (1.0 / ((abs(n.x) + abs(n.y)) + abs(n.z))));

    if (n.z < 0.0) {
        p = ((1.0 - abs(p.yx)) * float2(((p.x >= 0.0) ? 1.0 : -1.0), ((p.y >= 0.0) ? 1.0 : -1.0)));
    }

    return p;
}
// The inverse of sdfOctEncode: a [-1, 1]^2 octahedral point back to a unit direction.
float3 sdfOctDecode(float2 p) {
    float3 n = float3(p.x, p.y, (1.0 - (abs(p.x) + abs(p.y))));

    if (n.z < 0.0) {
        n.xy = ((1.0 - abs(n.yx)) * float2(((n.x >= 0.0) ? 1.0 : -1.0), ((n.y >= 0.0) ? 1.0 : -1.0)));
    }

    return normalize(n);
}

#endif
