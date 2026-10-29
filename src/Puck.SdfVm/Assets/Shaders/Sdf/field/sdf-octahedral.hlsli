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

// Maps a bilinear tap at most one texel past an edge back across the octahedron's identified edge.
// KEEP IN SYNC with SdfOctahedral.BorderSource (whose input includes a one-texel border).
int2 sdfOctBorderSource(int2 tap, int size) {
    int last = size - 1;
    bool mirrorX = (tap.x < 0) || (tap.x > last);
    bool mirrorY = (tap.y < 0) || (tap.y > last);
    int2 texel = tap;
    if (mirrorY) {
        texel = int2(last - clamp(tap.x, 0, last), (tap.y < 0) ? 0 : last);
    } else if (mirrorX) {
        texel = int2((tap.x < 0) ? 0 : last, last - clamp(tap.y, 0, last));
    }
    if (mirrorX && mirrorY) {
        texel = int2((tap.x < 0) ? last : 0, (tap.y < 0) ? last : 0);
    }
    return texel;
}

#endif
