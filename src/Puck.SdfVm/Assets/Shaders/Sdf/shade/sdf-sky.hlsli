// The procedural screen card, the star field, the cloud layer and the sky gradient.
#ifndef SHADE_SDF_SKY_HLSLI
#define SHADE_SDF_SKY_HLSLI
// Procedural placeholder for a SCREEN_SLAB face: an animated test-card.
float3 screenContent(float3 p, float time) {
    float bars = (0.5 + (0.5 * sin((p.y * 26.0) - (time * 5.0))));
    float3 baseColor = lerp(float3(0.02, 0.04, 0.09), float3(0.10, 0.80, 1.00), bars);
    float sweep = smoothstep(0.49, 0.5, frac((p.x * 1.3) + (time * 0.4)));

    return (baseColor + (0.35 * float3(0.95, 0.45, 0.12) * sweep));
}
// The star field's cell-grid domain is the octahedral sky projection (sdf-octahedral.hlsli).
#include "../field/sdf-octahedral.hlsli"
// The procedural star field: a per-cell PCG3D hash (seed folded in) over the octahedral sky projection picks
// StarSparsity of the cells to carry a star; two hash channels place the star inside its cell (kept StarInset from
// the walls so a disc never straddles a cell it is not tested in). A second hash of the first, paid only by the
// cells that carry a star, gives each its own apparent luminosity and colour: luminosity follows the count law of
// sources spread uniformly through space (N(>F) ∝ F^-3/2, so F = floor·u^-2/3, capped at the authored peak — most
// stars faint, a few bright, as the real sky reads), the disc growing mildly with it; colour is a blackbody tint
// picked log-uniformly in temperature from ~3000 K (orange) through ~6500 K (white) to ~15000 K (blue-white), each
// tint normalized to a unit peak channel so it colours the star without changing the luminosity law. Twinkling is
// optional: a hash-chosen share of the stars dip by the authored depth and recover, each riding its own small
// harmonic and phase of the authored period on the deterministic tick counter (reduced by an integer modulo first,
// so the phase is exact however long the session runs, and a replay at tick N twinkles identically). The disc is
// measured ANGULARLY — the star's cell point is decoded back to a direction and the pixel's angle to it compared
// against StarRadiusFraction of one cell's angular pitch (≈ π/density) — so a star is round everywhere on the sky
// rather than stretched by the projection's anisotropy. No texture, no session state — the identical (direction,
// seed) always draws the identical field.
static const float StarSparsity = 0.08;         // fraction of cells that carry a star
static const float StarInset = 0.3;             // star center's minimum distance from its cell walls, in cells
static const float StarRadiusFraction = 0.12;   // star angular radius as a fraction of one cell's angular pitch, at peak luminosity
static const float StarLuminosityFloor = 0.125; // the faintest star's luminosity as a fraction of the peak (~2.3 magnitudes)
static const float3 StarSpectrum[7] = {         // blackbody tints, unit peak channel: 3000, 4000, 5000, 6500, 8000, 10000, 15000 K
    float3(1.00, 0.71, 0.42),
    float3(1.00, 0.82, 0.64),
    float3(1.00, 0.89, 0.81),
    float3(1.00, 0.98, 0.99),
    float3(0.89, 0.91, 1.00),
    float3(0.79, 0.85, 1.00),
    float3(0.71, 0.80, 1.00)
};
float3 sdfStarField(float3 direction, float density, float brightness, uint seed, float twinkleShare, float twinkleDepth, uint twinklePeriodTicks, uint tick) {
    density = max(density, 1.0);

    float2 cellF = (((sdfOctEncode(direction) * 0.5) + 0.5) * density);
    float2 cellId = floor(cellF);
    uint3 h = sdfPcg3d(uint3(asuint(cellId.x), asuint(cellId.y), seed));
    float existence = ((float)h.x * SDF_INV_2POW32);

    if (existence > StarSparsity) {
        return float3(0.0, 0.0, 0.0);
    }

    uint3 h2 = sdfPcg3d(h);
    float luminosity = min(1.0, (StarLuminosityFloor * pow(max(((float)h2.x * SDF_INV_2POW32), 1e-6), -0.6666667)));
    float spectrum = (((float)h2.y * SDF_INV_2POW32) * 6.0);
    uint spectrumIndex = min((uint)spectrum, 5u);
    float3 tint = lerp(StarSpectrum[spectrumIndex], StarSpectrum[(spectrumIndex + 1u)], (spectrum - (float)spectrumIndex));

    if (((float)h2.z * SDF_INV_2POW32) < twinkleShare) {
        // Two sines at distinct small harmonics of the period, phase-offset per star, multiplied: an irregular dip
        // pattern that still closes exactly at the period boundary, so the integer modulo never shows a seam.
        uint3 h3 = sdfPcg3d(h2);
        float phase = ((float)(tick % twinklePeriodTicks) / (float)twinklePeriodTicks);
        float harmonicA = (float)(1u + (h3.x % 3u));
        float harmonicB = (float)(2u + (h3.y % 3u));
        float offset = ((float)h3.z * SDF_INV_2POW32);
        float flicker = (0.5 + (0.5 * (sin(6.28318531 * ((harmonicA * phase) + offset)) * sin(6.28318531 * ((harmonicB * phase) + (offset * 1.7))))));

        luminosity *= (1.0 - (twinkleDepth * flicker));
    }
    float2 starUv = lerp(StarInset.xx, (1.0 - StarInset).xx, float2(((float)h.y * SDF_INV_2POW32), ((float)h.z * SDF_INV_2POW32)));
    float3 starDirection = sdfOctDecode((((cellId + starUv) / density) * 2.0) - 1.0);
    // 1 - cos(angle) ≈ angle²/2 for the small angles a star subtends: compare against the radius squared over two, no acos.
    float radius = (((StarRadiusFraction * 3.14159265) / density) * lerp(0.6, 1.0, sqrt(luminosity)));
    float separation = (1.0 - dot(direction, starDirection));
    float coverage = smoothstep((0.5 * (radius * radius)), 0.0, separation);

    return (((coverage * brightness) * luminosity) * tint);
}
// Value noise on the integer lattice: one sdfPcg3d per corner (seed folded in), quintic-smoothed bilinear blend.
// The cell coordinates are hashed by their float bit patterns, so negative cells are as distinct as positive ones.
float sdfLatticeNoise(float2 p, uint seed) {
    float2 cell = floor(p);
    float2 f = (p - cell);
    float2 u = ((f * f * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float a = ((float)sdfPcg3d(uint3(asuint(cell.x), asuint(cell.y), seed)).x * SDF_INV_2POW32);
    float b = ((float)sdfPcg3d(uint3(asuint(cell.x + 1.0), asuint(cell.y), seed)).x * SDF_INV_2POW32);
    float c = ((float)sdfPcg3d(uint3(asuint(cell.x), asuint(cell.y + 1.0), seed)).x * SDF_INV_2POW32);
    float d = ((float)sdfPcg3d(uint3(asuint(cell.x + 1.0), asuint(cell.y + 1.0), seed)).x * SDF_INV_2POW32);

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}
// Four octaves of lattice noise (lacunarity 2, gain ½), each octave on its own seed, normalized to [0, 1].
float sdfCloudFbm(float2 p, uint seed) {
    float value = 0.0;
    float amplitude = 0.5;

    [unroll]
    for (uint octave = 0u; (octave < 4u); octave++) {
        value += (amplitude * sdfLatticeNoise(p, (seed + octave)));
        p = ((p * 2.0) + 17.0);
        amplitude *= 0.5;
    }

    return (value / 0.9375);
}
// The procedural cloud layer: a heightfield of cloud on a DOME — a spherical shell of radius CloudDomeRadius + 1
// about a centre CloudDomeRadius below the camera, unit height overhead. A direction's layer point is where its ray
// meets that shell (t = -R·d.y + sqrt(R²·d.y² + 2R + 1): 1 overhead, sqrt(2R + 1) at the horizon), so the layer
// compresses smoothly toward the horizon as a real one does and no direction ever needs a clamp — the vertical
// "curtain" smear a clamped plane projection painted under every horizon cloud is gone with it. The wind acts on
// that point before the noise reads it: the layer turns about the zenith by the host-integrated spin angle, is wound
// by the Coriolis curl (an extra rotation of curl · 2r/(1+r²) — zero at the zenith, peaking at 45° elevation, fading
// to the horizon — so bands spiral inward the way a rotating frame bends a broad flow, without the unbounded shear a
// rigid differential rotation would tear the field into), is scaled by the authored cell size and slid by the
// host-integrated drift. The THICKNESS at a point is a domain-warped fbm (a first fbm bends the second's domain by
// CloudWarp — the puffed, lobed silhouettes flat noise never gives), the shaping fbm read at its own host-integrated
// shear offset so the two fields slide past each other and the clouds boil and re-form as they travel, thresholded
// at (1 - coverage) over the authored softness. Volume is READ FROM THAT HEIGHTFIELD, four thickness taps per pixel:
// the centre tap and two offset taps give the field's gradient, hence a surface normal (CloudHeight tall per unit
// thickness) that lights against the lighting sun — sunward flanks bright, lee flanks dark; a fourth tap toward the
// sun finds a taller neighbour shadowing this point (CloudSelfShadow); the sun seen THROUGH a thin edge lines it in
// the sun's colour (CloudSilverLining); and the thickness sets the opacity through Beer's law (1 - exp(-t·CloudOpacity))
// so a core is solid and a fringe is a wisp. The layer fades to nothing in the last CloudHorizonFade of elevation
// (where its cells shrink past a pixel) and is never drawn below the horizon. Deterministic: (direction, settings,
// seed, host-integrated wind, sun) alone.
static const float CloudDomeRadius = 6.0;      // dome centre depth below the camera, in layer units (unit height overhead)
static const float CloudWarp = 0.6;            // how far the first fbm bends the second's domain, in cells
static const float CloudHeight = 0.7;          // the heightfield's rise per unit thickness, in cells — the normal's steepness
static const float CloudNormalTap = 0.18;      // the gradient taps' offset from the centre, in cells
static const float CloudSelfShadow = 0.6;      // how dark a point goes under a taller sunward neighbour
static const float CloudSilverLining = 0.5;    // the sun-through-a-thin-edge highlight's strength
static const float CloudOpacity = 3.5;         // Beer's-law extinction per unit thickness
static const float CloudHorizonFade = 0.05;    // direction.y below which the layer fades to nothing
float sdfCloudThickness(float2 p, float2 shearOffset, uint seed, float threshold, float softness) {
    float warp = sdfCloudFbm((p + shearOffset), (seed ^ 0x9E3779B9u));
    float density = sdfCloudFbm((p + (CloudWarp * (warp - 0.5))), seed);

    return smoothstep(threshold, (threshold + softness), density);
}
float4 sdfCloudLayer(float3 direction, float3 color, float coverage, float softness, float scale, uint seed, float2 offset, float2 shearOffset, float spinAngle, float curl) {
    if ((coverage <= 0.0) || (direction.y <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float b = (CloudDomeRadius * direction.y);
    float t = (sqrt((b * b) + ((2.0 * CloudDomeRadius) + 1.0)) - b);
    float2 layer = (direction.xz * t);
    float radius = length(layer);
    float angle = (spinAngle + (curl * ((2.0 * radius) / (1.0 + (radius * radius)))));
    float sinAngle;
    float cosAngle;

    sincos(angle, sinAngle, cosAngle);

    float2 turned = float2(((layer.x * cosAngle) - (layer.y * sinAngle)), ((layer.x * sinAngle) + (layer.y * cosAngle)));
    float2 p = ((turned / max(scale, 1e-3)) + offset);
    float threshold = (1.0 - coverage);
    float thickness = sdfCloudThickness(p, shearOffset, seed, threshold, softness);

    if (thickness <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // The sun in the layer's turned frame (the same rotation the layer point took), so the lighting follows the wind.
    float3 sun = worldSunDirection();
    float2 sunTurned = float2(((sun.x * cosAngle) - (sun.z * sinAngle)), ((sun.x * sinAngle) + (sun.z * cosAngle)));
    float thicknessX = sdfCloudThickness((p + float2(CloudNormalTap, 0.0)), shearOffset, seed, threshold, softness);
    float thicknessY = sdfCloudThickness((p + float2(0.0, CloudNormalTap)), shearOffset, seed, threshold, softness);
    float thicknessSunward = sdfCloudThickness((p + (CloudNormalTap * 2.0 * normalize(sunTurned + 1e-5))), shearOffset, seed, threshold, softness);
    float3 normal = normalize(float3(-((thicknessX - thickness) / CloudNormalTap) * CloudHeight, 1.0, -((thicknessY - thickness) / CloudNormalTap) * CloudHeight));
    float diffuse = saturate(dot(normal, normalize(float3(sunTurned.x, sun.y, sunTurned.y))));
    float shadow = (1.0 - (CloudSelfShadow * saturate(thicknessSunward - thickness)));
    float lining = ((CloudSilverLining * pow(saturate(dot(direction, sun)), 8.0)) * (1.0 - thickness));
    float3 shade = ((color * (lerp(0.45, 1.0, diffuse) * shadow)) + (worldSunColor() * lining));
    float alpha = ((1.0 - exp(-(thickness * CloudOpacity))) * smoothstep(0.0, CloudHorizonFade, direction.y));

    return float4(shade, alpha);
}
// The sky's GRADIENT alone — what distance fog and the silhouette-edge blend fade toward. The sun disc, stars and
// clouds are miss-pixel content (skyColor); folding them into fog would let a low sun bleed through a fogged floor.
float3 skyGradient(float3 direction) {
    if (!worldSkyEnabled()) {
        // The pinned two-stop gradient, UNCHANGED from before render.sky existed: the identical instructions in the
        // identical order, so a world that never authors render.sky renders bit-identically.
        float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

        return lerp(float3(0.04, 0.05, 0.07), float3(0.10, 0.13, 0.20), t);
    }

    // The authored stops, ascending in elevation (the validator orders them): piecewise-linear in direction.y,
    // clamped to the end stops beyond the first and last.
    uint stops = worldSkyStopCount();
    float elevation = direction.y;
    float4 previous = worldSkyStop(0u);

    if ((stops <= 1u) || (elevation <= previous.w)) {
        return previous.rgb;
    }

    [loop]
    for (uint index = 1u; (index < stops); index++) {
        float4 next = worldSkyStop(index);

        if (elevation <= next.w) {
            float t = saturate((elevation - previous.w) / max((next.w - previous.w), 1.0e-5));

            return lerp(previous.rgb, next.rgb, t);
        }

        previous = next;
    }

    return previous.rgb;
}
float3 skyColor(float3 direction) {
    float3 color = skyGradient(direction);

    if (!worldSkyEnabled()) {
        return color;
    }

    // The sun disc: an additive pow(cosAngle, k) highlight about its light's direction. k is HOST-BAKED from the
    // authored angular radius (SdfWorldEngine.PackEnvironment) so this pays one pow() rather than deriving the
    // exponent from an angle per pixel.
    int discLight = worldSkySunDiscLightIndex();

    if (discLight >= 0) {
        float cosAngle = dot(direction, worldLight((uint)discLight).direction);

        color += (worldSkySunDiscIntensity() * pow(saturate(cosAngle), worldSkySunDiscExponent())).xxx;
    }

    // Stars read only above the local horizon — a night sky under the ground plane is never visible to the camera
    // and would otherwise tile through geometry for nothing.
    if (direction.y > 0.0) {
        color += sdfStarField(direction, worldSkyStarDensity(), worldSkyStarBrightness(), worldSkyStarSeed(), worldSkyStarTwinkleShare(), worldSkyStarTwinkleDepth(), worldSkyStarTwinklePeriodTicks(), passGroup.sampleIndex);
    }

    // Clouds sit over everything above them — the gradient, the sun disc and the stars — by their own coverage mask.
    float4 clouds = sdfCloudLayer(direction, worldSkyCloudColor(), worldSkyCloudCoverage(), worldSkyCloudSoftness(), worldSkyCloudScale(), worldSkyCloudSeed(), worldSkyCloudOffset(), worldSkyCloudShearOffset(), worldSkyCloudSpinAngle(), worldSkyCloudCurl());

    return lerp(color, clouds.rgb, clouds.a);
}
// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

#endif
