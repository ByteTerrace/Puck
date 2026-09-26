// The environment and screen-light rows the frame carries: their layout, the screen surfaces' frames, which screen
// slots hold a source this frame, which screen hits shade as a screen, and the row reader.
#ifndef FRAME_SDF_ENVIRONMENT_HLSLI
#define FRAME_SDF_ENVIRONMENT_HLSLI
// The ENVIRONMENT block: SdfEnvironment's lanes, row for row, after the far-field row. KEEP IN SYNC with
// SdfEnvironment (row layout, blend kinds) and SdfWorldEngine.PackEnvironment (the host bakes: unit directions, the
// sun-disc exponent, the twinkle period, the integrated cloud offsets and spin).
static const uint SdfEnvBase = 40u;
static const uint SdfEnvControl = (SdfEnvBase + 0u);     // x light count, y shadow light index (-1 none), z sky enabled, w fog density
static const uint SdfEnvLights = (SdfEnvBase + 1u);      // 3 rows per light: (direction.xyz - a position for a point light, weight) (color.rgb, kind) (param, shadows, dynamicSlot - point only else 0, 0)
static const uint SdfEnvMaxLights = 8u;
static const uint SdfEnvRowsPerLight = 3u;
static const uint SdfEnvCurvatureA = (SdfEnvBase + 25u); // cavity, rim, ink, ink band low
static const uint SdfEnvCurvatureB = (SdfEnvBase + 26u); // ink color.rgb, ink band high
static const uint SdfEnvSkyControl = (SdfEnvBase + 27u); // x gradient stop count, y sun-disc light index (-1 none), z sun-disc pow() exponent, w sun-disc intensity
static const uint SdfEnvSkyStops = (SdfEnvBase + 28u);   // 4 rows: color.rgb, elevation in [-1, 1], ascending
static const uint SdfEnvStars = (SdfEnvBase + 32u);      // density, brightness, seed, 0
static const uint SdfEnvTwinkle = (SdfEnvBase + 33u);    // share, depth, period in engine ticks, 0
static const uint SdfEnvCloudsA = (SdfEnvBase + 34u);    // color.rgb, coverage
static const uint SdfEnvCloudsB = (SdfEnvBase + 35u);    // softness, scale, seed, 0
static const uint SdfEnvCloudsC = (SdfEnvBase + 36u);    // layer offset.xy, shaping offset.xy
static const uint SdfEnvCloudsD = (SdfEnvBase + 37u);    // spin angle, curl, 0, 0
static const uint SdfEnvSoftboxControl = (SdfEnvBase + 38u); // x softbox count, y tonemap mode (0 none, 1 filmic), 0, 0
static const uint SdfEnvSoftboxes = (SdfEnvBase + 39u);   // 3 rows per softbox: (direction.xyz, weight) (color.rgb, sizeW) (sizeH, blur, 0, 0)
static const uint SdfEnvMaxSoftboxes = 4u;
static const uint SdfEnvRowsPerSoftbox = 3u;
static const uint SdfEnvHorizonLow = (SdfEnvBase + 51u);  // studio reflection horizon low (ground-ward) color.rgb
static const uint SdfEnvHorizonHigh = (SdfEnvBase + 52u); // studio reflection horizon high (sky-ward) color.rgb
static const uint SdfEnvLightDirectional = 0u;
static const uint SdfEnvLightHemisphere = 1u;
static const uint SdfEnvLightRim = 2u;
static const uint SdfEnvLightPoint = 3u;
static const uint SdfEnvLightOccluder = 4u;
static const uint SdfTonemapNone = 0u;
static const uint SdfTonemapFilmic = 1u;

#ifdef SDF_SCREEN_SOURCES
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SignedDistance.SdfScreenSurface) — Stage 1 ONLY
// (binding 10/11 are not part of the beam prepass's descriptor set). Indexed DIRECTLY by screen index
// (0..31, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
};
static const uint WorldScreenSurfaceRows = 3u;
ScreenSurfaceData worldScreenSurface(uint screenIndex) {
    uint row = (screenIndex * WorldScreenSurfaceRows);
    ScreenSurfaceData data;
    data.right = screenSurfaces[row];
    data.up = screenSurfaces[(row + 1u)];
    data.origin = screenSurfaces[(row + 2u)];
    return data;
}
// The screenSurfaces[] / sdfDecalCells[] / screenSourceN entry count — the width every screen index is bounded
// against before it indexes one. KEEP IN SYNC with SdfProgramBuilder.MaxScreenSurfaces.
static const uint SdfScreenSurfaceCount = 32u;
// The screen source images — one sampled image per screen index (screenSource0..screenSource31), all read through the
// views set's one nearest screenSampler, so emulator pixels stay crisp. Slots with no source bound this frame
// (passGroup.screenMask bit clear) hold a valid filler view; the shader never samples an unbound slot (screenSourceBound
// gates it), so the filler's content never reaches the image. (screenMask is a single uint, so exactly 32 screen bits
// fit — raising past 32 needs a second mask word.)
// Per-frame screen LIGHT records (sdfScreenLights): entries 0..31 carry each
// screen's emitted light (rgb = the framebuffer's average color this frame, a = intensity gain), entry 32 is the
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; z/w = the SLICE debug view's
// plane selector: z = axis (0 camera-locked, 1/2/3 world X/Y/Z), w = the axis plane's signed offset — see
// SdfFrame.DebugSliceAxis; read only by debug view mode 7). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
static const uint SdfScreenLightEnv = SdfScreenSurfaceCount;

// Grid-lock overlay rows (grid-locking §4a): FOUR float4 rows AFTER the env entry (env stays at 32 — load-bearing as
// the screen-count loop bound above). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's Grid* fields.
static const uint SdfGridWorld = 33u;      // x = flags (bit0 world floor grid, bit1 object grid), y = floorY, zw = world pitch (X, Z)
static const uint SdfGridObjOrigin = 34u; // xyz = reference origin (world), w = object pitch X
static const uint SdfGridObjFrame = 35u;  // xyzw = reference frame quaternion
static const uint SdfGridObjParams = 36u; // x = object pitch Z, y = patch radius (reference-local), z = analytic-normal A/B, w = shadow-cull A/B
// Engine-bench shader-feature params: x = disable soft shadows, y = disable AO, z = shadow-distance
// scale (0 = the full 1.0 reach), w = disable screen lights. KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's
// DisableSoftShadows/DisableAmbientOcclusion/ShadowDistanceScale/DisableScreenLights fields.
static const uint SdfBenchParams = 37u;
// The engine-bench SHADOW-PROXY params row (PATH B): x = enable the shadow proxy (shadow rays skip Subtraction-family
// carve instances and march the pre-carve union hull — sdf.shadow-proxy; 0 = OFF, the default, so an unset frame uploads
// 0 and is byte-identical); y = use the camera-tile shadow mask instead of the per-pixel shadow-grid gather; z = use the
// bounded-cost fast soft-shadow marcher; w
// reserved. A SEPARATE row from SdfBenchParams (whose four lanes are full). KEEP IN SYNC with
// SdfWorldEngine.PackScreenLights + SdfFrame's EnableShadowProxy/UseCameraTileShadowMask/UseFastSoftShadowMarch fields.
static const uint SdfShadowProxyParams = 38u;
// The F1 FAR-FIELD lever row: x = disable the beam-published per-tile far bound (1 = the A/B
// "off" side — the fine march ignores plane 3 and runs to the far distance exactly as pre-F1; 0 = the DEFAULT shipped
// behavior with the far bound ACTIVE, so an unset frame uploads 0 and the feature is ON); y = disable the F2 shadow
// light-side exit (RESERVED for F2, not yet consumed); zw reserved. A SEPARATE row from SdfShadowProxyParams (whose
// lanes carry the shadow proxy). KEEP IN SYNC with SdfWorldEngine.PackScreenLights + SdfFrame's DisableFarBound field.
static const uint SdfFarFieldParams = 39u;

float4 worldEnvRow(uint row) { return sdfScreenLights[row]; }

bool screenSourceBound(uint screenIndex) {
    return (0u != (passGroup.screenMask & (1u << screenIndex)));
}
// One past the highest bound screen slot (0 when screenMask is 0) — firstbithigh(0) is undefined, so that case is
// guarded explicitly rather than relied on to return -1.
uint screenLightLoopBound() {
    return ((0u == passGroup.screenMask) ? 0u : (firstbithigh(passGroup.screenMask) + 1u));
}
// Whether a hit on this material shades as a screen: a screen-instance id (above SDF_SCREEN_MATERIAL, from
// SdfProgramBuilder's screen-surface ScreenSlab overload) whose slot carries a glyph decal or a source bound this frame.
// sampleScreenSurface shades exactly these hits, and the surface pass leaves them without a normal.
bool sdfScreenSurfaceShades(int material) {
    if (material <= SDF_SCREEN_MATERIAL) {
        return false; // the plain sentinel: no declared instance, so no screen table lookup.
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

    // An out-of-bounds structured-buffer read is zeroed on Direct3D 12 by spec but only defined under robustBufferAccess
    // on Vulkan, so the bound makes both backends agree by construction rather than by driver luck. Falling back to the
    // material-shaded path is the same answer a zeroed entry would produce here (no decal, no bound source), and the host
    // refuses such an id, so no valid program reaches this branch and no composed pixel moves.
    if (screenIndex >= SdfScreenSurfaceCount) {
        return false;
    }

#if defined(SDF_GLYPH_ATLAS)
    uint4 decal = sdfDecalCells[screenIndex];

    if ((decal.x > 0u) && (decal.y > 0u)) {
        return true;
    }
#endif

    // Declared, but neither a decal nor a bound source this frame: the material-shaded fallback applies.
    return screenSourceBound(screenIndex);
}
#else
// The environment reader's no-screen-sources half: the pinned sun and hemisphere an unauthored world renders, so a
// kernel that binds no screen-light buffer still parses the lit path and agrees with the bound one whenever a world
// authors no lighting. KEEP IN SYNC with SdfEnvironment.Default.
float4 worldEnvRow(uint row) {
    uint offset = (row - SdfEnvBase);

    if (offset == 0u) { return float4(2.0, 0.0, 0.0, 0.015); }
    if (offset == 1u) { return float4(SdfSunDirection, 0.85); }
    if (offset == 2u) { return float4(1.0, 1.0, 1.0, 0.0); }
    if (offset == 3u) { return float4((1.0 / 9.0), 1.0, 0.0, 0.0); }
    if (offset == 4u) { return float4(0.0, 0.0, 0.0, 0.25); }
    if (offset == 5u) { return float4(1.0, 1.0, 1.0, 1.0); }
    if (offset == 6u) { return float4(0.25, 0.0, 0.0, 0.0); }
    if (offset == 25u) { return float4(0.0, 0.0, 0.0, 6.0); }
    if (offset == 26u) { return float4(0.02, 0.02, 0.03, 16.0); }
    if (offset == 27u) { return float4(0.0, -1.0, 1.0, 0.0); }

    return float4(0.0, 0.0, 0.0, 0.0);
}
#endif

#endif
