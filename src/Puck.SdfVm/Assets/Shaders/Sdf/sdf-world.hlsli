// Shared data contract + shading for the SDF world compositor compute kernels: sdf-beam.comp (the tile-cull
// prepass) and sdf-world-views.comp (Stage 1, per-view rendering; sdf-world-composite.comp does Stage 2). Both run
// the generic VM (sdf-vm.hlsli) over the scene program and a table of viewports/cameras supplied as DATA. KEEP IN
// SYNC with SdfWorldEngine's packing and with sdf-view.frag.hlsl's shading.
#ifndef SDF_WORLD_HLSLI
#define SDF_WORLD_HLSLI
#include "sdf-vm.hlsli"

// The viewport table — cameras + regions — as DATA (binding 2). sdf-vm.hlsli binds the scene program at binding 1.
struct ViewportData {
    float4 position;    // xyz = world position, w = time (seconds)
    float4 right;       // xyz = right basis,   w = tan(fov / 2)
    float4 up;          // xyz = up basis,      w = aspect ratio
    float4 forward;     // xyz = forward basis, w = debug view mode (0 = final)
    float4 region;      // xy = normalized origin, zw = normalized size (of the output image)
};
[[vk::binding(2, 0)]] StructuredBuffer<ViewportData> viewports : register(t1);

struct CompositeParams {
    uint2 imageExtent;   // output image size in pixels
    uint2 tileGrid;      // tiles per viewport (row, column) — the cull buffer's per-viewport stride
    uint viewportCount;
    uint childMask;      // bit v set => viewport v is backed by a CHILD node's surface, not an SDF camera
    uint screenMask;     // bit s set => screen source slot s is bound this frame (Stage 1 only; unused elsewhere)
    uint instanceMaskWordCount; // the LIVE uploaded program's derived per-tile mask width (SdfProgram.InstanceMaskWordCount), pushed per frame
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

// Whether viewport v is a hosted child surface (its source[] slot holds another node's output): the beam prepass
// and Stage 1 skip such slots so the SDF render never overwrites the child's pixels.
bool isChildViewport(uint viewportIndex) {
    return (0u != (params.childMask & (1u << viewportIndex)));
}
uint worldInstanceMaskBase(uint tileIndex) {
    return (params.instanceMaskWordCount * tileIndex);
}

#ifdef SDF_SCREEN_SOURCES
// A declared ScreenSlab instance's world-space front-face frame (see Puck.SdfVm.SdfScreenSurface) — Stage 1 ONLY
// (binding 10/11 are not part of the beam prepass or Stage 2's descriptor sets). Indexed DIRECTLY by screen index
// (0..3, the same slot SetScreenSource/screenSources binds) — not by declaration order — so a hit resolves its
// surface with no search; an unfilled slot's entry is never read (no material id can address it: the host packs an
// entry only when SdfProgramBuilder registers that screen index).
struct ScreenSurfaceData {
    float4 right;   // xyz = unit world-space U axis, w = half-width
    float4 up;      // xyz = unit world-space V axis (V=0 at top), w = half-height
    float4 origin;  // xyz = world-space front-face center, w = unused (pad)
};
[[vk::binding(10, 0)]] StructuredBuffer<ScreenSurfaceData> screenSurfaces : register(t4);
// The screen source images (nearest-filtered, so emulator/child pixels stay crisp) — one per screen index (0..3), FOUR
// separate combined-image-sampler bindings (12..15; DXC's vk::combinedImageSampler does not support an ARRAY texture,
// only a scalar one, so a true single Vulkan combined-image-sampler array isn't expressible in this HLSL — see the
// C# side for the derived binding indices). Each Texture2D+SamplerState pair shares one binding (fusing into ONE
// Vulkan combined-image-sampler descriptor) and needs its OWN sampler register (s0..s3) — DXC rejects two distinct
// sampler declarations aliased onto one register — so Direct3D 12 bakes in FOUR static samplers, one per
// SampledImage binding, ALL with the identical requested filter (NEAREST): logically one shared sampler, materialized
// as four registers because the shading language has no array-of-combined-image-sampler here. Direct3D 12 sees four
// SRVs (t5..t8). Slots with no source bound this frame (params.screenMask bit clear) duplicate a valid filler view;
// the shader never samples an unbound slot (screenSourceBound gates it), so the filler's content never reaches the
// image.
[[vk::combinedImageSampler]] [[vk::binding(12, 0)]] Texture2D<float4> screenSource0 : register(t5);
[[vk::combinedImageSampler]] [[vk::binding(12, 0)]] SamplerState screenSampler0 : register(s0);
[[vk::combinedImageSampler]] [[vk::binding(13, 0)]] Texture2D<float4> screenSource1 : register(t6);
[[vk::combinedImageSampler]] [[vk::binding(13, 0)]] SamplerState screenSampler1 : register(s1);
[[vk::combinedImageSampler]] [[vk::binding(14, 0)]] Texture2D<float4> screenSource2 : register(t7);
[[vk::combinedImageSampler]] [[vk::binding(14, 0)]] SamplerState screenSampler2 : register(s2);
[[vk::combinedImageSampler]] [[vk::binding(15, 0)]] Texture2D<float4> screenSource3 : register(t8);
[[vk::combinedImageSampler]] [[vk::binding(15, 0)]] SamplerState screenSampler3 : register(s3);
// Per-frame screen LIGHT records (binding 11, register t10 — the LAST SRV in the views set): entries 0..3 carry each
// screen's emitted light (rgb = the framebuffer's average color this frame, a = intensity gain), entry 4 is the
// ENVIRONMENT (x = ambient scale, y = sun scale — dim the room so the glow dominates; zw pad). A light's geometry
// (position/orientation/extent) is the SAME screenSurfaces[i] entry above — a screen is an area emitter, so it needs
// only its color here. KEEP IN SYNC with SdfWorldEngine's screen-light buffer packing.
[[vk::binding(11, 0)]] StructuredBuffer<float4> sdfScreenLights : register(t10);
static const uint SdfScreenLightEnv = 4u;

// CRT glass-face constants — a FLAT SQUARE tube (the legendary Trinitron look): NO pincushion bulge, near-square
// corners, a thin crisp dark bezel, subtle native-line scanlines, and a soft bright-pixel bloom knee. No corner
// vignette and no fresnel glint, so the screen reads dead-flat and even — a game on it looks almost exactly like a
// real GB/GBA panel scaled up. All continuous (smoothstep/cos), so a cross-backend ±1-LSB UV delta never flips a hard
// edge. ScreenLightFalloff is the room glow's inverse-square softening.
static const float CrtCurvature = 0.0;    // flat glass (was 0.12 pincushion)
static const float CrtBezel = 0.03;       // a thin Trinitron bezel
static const float CrtCornerRadius = 0.004; // near-square corners (was 0.045 rounded)
static const float CrtBezelSoft = 0.008;  // a crisp bezel edge
static const float CrtScanAmplitude = 0.06; // subtle scanlines — a hint of CRT, not a filter
static const float CrtScanLines = 144.0;
static const float CrtVignette = 0.0;     // flat, even brightness (was 0.35)
static const float CrtBloomGain = 0.5;
static const float CrtBloomThreshold = 0.6;
static const float CrtGlint = 0.0;        // no glass glint (was 0.06)
static const float CrtGlintPower = 3.0;
static const float ScreenLightFalloff = 0.28;

bool screenSourceBound(uint screenIndex) {
    return (0u != (params.screenMask & (1u << screenIndex)));
}
float4 sampleScreenSource(uint screenIndex, float2 uv) {
    // Every screenSamplerN carries the SAME filter (NEAREST) — the four-way split is purely to give DXC one sampler
    // symbol per register; there is exactly one LOGICAL sampler behavior on either backend.
    switch (screenIndex) {
        case 0:  return screenSource0.SampleLevel(screenSampler0, uv, 0);
        case 1:  return screenSource1.SampleLevel(screenSampler1, uv, 0);
        case 2:  return screenSource2.SampleLevel(screenSampler2, uv, 0);
        default: return screenSource3.SampleLevel(screenSampler3, uv, 0);
    }
}
// For a screen-instance material id (> SDF_SCREEN_MATERIAL, from SdfProgramBuilder's screen-surface ScreenSlab
// overload) whose screen index has a source bound THIS FRAME, samples it (NEAREST) at the hit point's UV. outColor
// is valid only when this returns true; the caller falls back to exactly today's flat/procedural screen shading
// otherwise (the plain sentinel, or a declared surface with no source bound this frame).
bool sampleScreenSurface(int material, float3 hitPoint, float3 rayDirection, out float3 outColor) {
    outColor = float3(0.0, 0.0, 0.0);

    if (material <= SDF_SCREEN_MATERIAL) {
        return false; // the plain sentinel: no declared instance, so no screen table lookup.
    }

    uint screenIndex = (uint)(material - SDF_SCREEN_MATERIAL - 1);

    if (!screenSourceBound(screenIndex)) {
        return false; // declared, but no source bound this frame — the material-shaded fallback applies.
    }

    ScreenSurfaceData surface = screenSurfaces[screenIndex];
    float3 local = (hitPoint - surface.origin.xyz);
    float2 uv = float2(
        (0.5 + (0.5 * (dot(local, surface.right.xyz) / surface.right.w))),
        (0.5 - (0.5 * (dot(local, surface.up.xyz) / surface.up.w)))
    );

    // Bulge the image out about the screen center (pincushion), so it reads as curved tube glass rather than a decal.
    float2 centered = (uv - 0.5);
    float radiusSquared = dot(centered, centered);
    float2 curved = (0.5 + (centered * (1.0 + (CrtCurvature * radiusSquared))));

    // Rounded-corner bezel: a smooth rounded-rect mask (SDF on the curved uv) that fades to black just inside the slab
    // edge — the bulge pushes the corners past it, giving the dark rounded corners of a real tube.
    float2 edgeDistance = ((abs(curved - 0.5) - float2((0.5 - CrtBezel), (0.5 - CrtBezel))) + CrtCornerRadius);
    float outside = (length(max(edgeDistance, 0.0)) - CrtCornerRadius);
    float bezel = (1.0 - smoothstep(0.0, CrtBezelSoft, outside));

    float3 sampled = sampleScreenSource(screenIndex, saturate(curved)).rgb;

    // Native-line scanlines (soft cosine) + radial vignette.
    float scanline = (1.0 - (CrtScanAmplitude * (0.5 - (0.5 * cos(((curved.y * CrtScanLines) * 6.2831853))))));
    float vignette = (1.0 - (CrtVignette * radiusSquared));

    // Bloom knee: bright pixels bleed a little (single-pixel fake — no neighborhood pass).
    float luminance = dot(sampled, float3(0.299, 0.587, 0.114));
    sampled += ((CrtBloomGain * smoothstep(CrtBloomThreshold, 1.0, luminance)) * sampled);

    // Fresnel glass glint: a faint rim brighten at glancing view angles (light catching the tube glass).
    float3 screenNormal = normalize(cross(surface.right.xyz, surface.up.xyz));
    float glint = pow((1.0 - saturate(dot(-rayDirection, screenNormal))), CrtGlintPower);

    outColor = ((((sampled * scanline) * vignette) * bezel) + (CrtGlint * glint));

    return true;
}
#endif

static const int MaxSteps = 160;
static const float MaxDistance = 60.0;
static const float SurfaceEpsilon = 0.001;
static const uint WorldTileSize = 16u;
static const int ConeMarchSteps = 96;
static const float ConeNear = 0.02;
static const float ConeEpsilon = 0.002;
static const float TileEmpty = -1.0; // sentinel: the cone clears the field — no ray in the tile can hit

// The 6-tap normal probe, MASKED (world path): every tap shares the pixel's tile instance mask — sound because a
// masked-out instance is exactly as absent from a nearby tap as it is from the hit itself (the beam prepass's tile
// cone covers the whole tile, taps included at this epsilon).
float3 calculateNormal(float3 p, uint instanceMaskBase) {
    float2 e = float2(0.0006, 0.0);

    return normalize(float3(
        (mapMasked(p + e.xyy, instanceMaskBase).distance - mapMasked(p - e.xyy, instanceMaskBase).distance),
        (mapMasked(p + e.yxy, instanceMaskBase).distance - mapMasked(p - e.yxy, instanceMaskBase).distance),
        (mapMasked(p + e.yyx, instanceMaskBase).distance - mapMasked(p - e.yyx, instanceMaskBase).distance)
    ));
}
// Procedural placeholder for a SCREEN_SLAB face: an animated test-card.
float3 screenContent(float3 p, float time) {
    float bars = (0.5 + (0.5 * sin((p.y * 26.0) - (time * 5.0))));
    float3 baseColor = lerp(float3(0.02, 0.04, 0.09), float3(0.10, 0.80, 1.00), bars);
    float sweep = smoothstep(0.49, 0.5, frac((p.x * 1.3) + (time * 0.4)));

    return (baseColor + (0.35 * float3(0.95, 0.45, 0.12) * sweep));
}
float3 skyColor(float3 direction) {
    float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

    return lerp(float3(0.04, 0.05, 0.07), float3(0.10, 0.13, 0.20), t);
}
// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read clearly
// in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

// The perspective ray for a viewport-local UV (pixel centers in [0,1] within the viewport's region; screen-up maps
// to the camera's +up).
float3 cameraRayDirection(ViewportData view, float2 localUv) {
    float2 ndc = ((localUv * 2.0) - 1.0);

    ndc.y = -ndc.y;

    float tanHalfFov = view.right.w;
    float aspect = view.up.w;

    return normalize(
        view.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * view.right.xyz) +
        ((ndc.y * tanHalfFov) * view.up.xyz)
    );
}

uint worldTileIndex(uint viewportIndex, uint2 tileCoord, uint2 tileGrid) {
    return ((viewportIndex * (tileGrid.x * tileGrid.y)) + (tileCoord.y * tileGrid.x) + tileCoord.x);
}

struct TileCone {
    float3 centerDirection;
    float chord;
};

TileCone buildTileCone(ViewportData view, float2 localUvMin, float2 localUvMax) {
    TileCone cone;

    cone.centerDirection = cameraRayDirection(view, (0.5 * (localUvMin + localUvMax)));
    cone.chord = 0.0;
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMin) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMax.x, localUvMin.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, float2(localUvMin.x, localUvMax.y)) - cone.centerDirection));
    cone.chord = max(cone.chord, length(cameraRayDirection(view, localUvMax) - cone.centerDirection));

    return cone;
}

// Conservative cone (beam) march over the distance field for a tile's UV rect. map() is a true distance field, so
// the cone of half-spread `chord` clears ALL of its rays while map(center) - chord*t > 0, and a 1-Lipschitz-safe
// step is clearance / (1 + chord). Returns the earliest t at which any ray in the tile could hit — a march-start
// lower bound shared by every pixel in the tile — or TileEmpty when the cone clears the field out to MaxDistance.
float coneMarchTile(ViewportData view, TileCone cone) {
    float3 origin = view.position.xyz;

    float t = ConeNear;

    [loop]
    for (int i = 0; (i < ConeMarchSteps); i++) {
        float clearance = (map(origin + (cone.centerDirection * t)).distance - (cone.chord * t));

        if (clearance <= ConeEpsilon) {
            return t;
        }

        t += (clearance / (1.0 + cone.chord));

        if (t > MaxDistance) {
            return TileEmpty;
        }
    }

    return t;
}

// Per-tile instance cull (the beam prepass, world path only — requires SDF_DYNAMIC_TRANSFORMS for a DYNAMIC
// instance's bound to resolve): tests the 32 instances of one mask WORD's index range against the tile's cone (the
// same center ray + chord coneMarchTile already computed) and sets an instance's bit when its world-space bounding
// sphere may be visible to ANY ray in the tile — the beam kernel calls this once per derived mask word
// (sdfInstanceMaskWordCount) and writes each word straight to the mask buffer. `instanceOffset` is the instance
// directory's offset (sdfInstanceDirectoryOffset), resolved ONCE by the caller so the per-instance bound loads skip
// the loop-invariant offset chain. Mirrors coneMarchTile's own per-ray cone-vs-field soundness argument, but here
// the "field" is a single analytic sphere: a ray at parameter t lies within (chord * t) of the center ray, so a
// sphere whose axis distance from the center ray exceeds (radius + chord * t) at the sphere's own closest approach
// cannot be hit by any ray in the tile.
uint collectInstanceMaskWord(uint instanceOffset, uint wordIndex, uint instanceCount, float3 rayOrigin, float3 centerDirection, float chord) {
    uint bits = 0u;
    uint first = (wordIndex << 5u);
    uint end = min((first + 32u), instanceCount);

    [loop]
    for (uint i = first; (i < end); i++) {
        float4 bound = sdfInstanceBoundAt(instanceOffset, i);
        float3 toCenter = (bound.xyz - rayOrigin);
        float alongRay = max(dot(toCenter, centerDirection), 0.0);
        float axisDistance = length(toCenter - (centerDirection * alongRay));

        if (axisDistance <= (bound.w + (chord * alongRay))) {
            bits |= (1u << (i - first));
        }
    }

    return bits;
}

// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
// `instanceMaskBase` is the pixel's tile mask base in the mask buffer (SDF_INSTANCE_MASK_ALL when the beam prepass
// never resolved one, e.g. a consumer that skips it) — the WHOLE march (and its normal probe) uses the SAME mask
// throughout, so the masked field a ray marches through is self-consistent start to finish.
// The debug-view-mode wire contract: viewport forward.w carries the mode index into DebugViewModes.Names
// (src/Puck.Demo/DebugView.cs — the list's ORDER is the wire value; KEEP IN SYNC, including the switch below).
// Mode 0 / >= DebugViewModeCount render final shading.
static const int DebugViewModeCount = 6;
static const int DebugViewModeNormals = 2;

float3 renderView(ViewportData view, float2 localUv, float marchStart, uint instanceMaskBase) {
    float3 rayOrigin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, localUv);
    int viewMode = (int)round(view.forward.w);
    float time = view.position.w;

    float traveled = max(marchStart, 0.0);
    bool hitSurface = false;
    int material = 0;
    int marchStep = 0;

    if (marchStart >= 0.0) {
        [loop]
        for (marchStep = 0; (marchStep < MaxSteps); marchStep++) {
            SdfHit hit = mapMasked(rayOrigin + (rayDirection * traveled), instanceMaskBase);

            if (hit.distance < SurfaceEpsilon) {
                hitSurface = true;
                material = hit.material;
                break;
            }

            traveled += hit.distance;

            if (traveled > MaxDistance) {
                break;
            }
        }
    }

    float3 normal = float3(0.0, 0.0, 0.0);
    float3 color = skyColor(rayDirection);

    if (hitSurface) {
        float3 surfacePoint = (rayOrigin + (rayDirection * traveled));
        bool useFinalShading = ((viewMode <= 0) || (viewMode >= DebugViewModeCount));
        bool sampledScreen = false;
#ifdef SDF_SCREEN_SOURCES
        if (useFinalShading) {
            // A bound screen source wins over BOTH the flat sentinel and the procedural test-card: emissive/unlit
            // (the diegetic screen is its own light source, like a real display — no scene lighting dims or tints it),
            // but shaped by the CRT glass face (curvature/bezel/scanlines/vignette/glint/bloom in sampleScreenSurface)
            // before the shared distance fog. The screen ALSO lights the room — see the screen-light loop below.
            sampledScreen = sampleScreenSurface(material, surfacePoint, rayDirection, color);
        }
#endif

        bool needsLitColor = (useFinalShading && !sampledScreen);
        bool needsNormal = ((viewMode == DebugViewModeNormals) || needsLitColor);

        if (needsNormal) {
            normal = calculateNormal(surfacePoint, instanceMaskBase);
        }

        if (needsLitColor) {
            float3 sunDirection = normalize(float3(0.55, 0.85, 0.35));
            float sunDiffuse = max(dot(normal, sunDirection), 0.0);
            float ambient = (0.25 + (0.25 * normal.y));

            // The environment scales dim the room so the diegetic screen glow dominates. They default to 1 outside the
            // world-views path (every other path shades exactly as before); the overworld sets them low per frame.
            float ambientScale = 1.0;
            float sunScale = 1.0;
#ifdef SDF_SCREEN_SOURCES
            float4 environment = sdfScreenLights[SdfScreenLightEnv];
            ambientScale = environment.x;
            sunScale = environment.y;
#endif

            float3 radiance = (float3(1.0, 1.0, 1.0) * ((ambient * ambientScale) + ((0.85 * sunDiffuse) * sunScale)));

#ifdef SDF_SCREEN_SOURCES
            // Every BOUND diegetic screen is a colored area light: its position/orientation come from the
            // screen-surface table, its color from the per-frame framebuffer average. The dot(screenNormal, -L) gate
            // is the "light through the glass" cue — a screen only lights what sits in front of its face.
            for (uint lightIndex = 0u; (lightIndex < 4u); lightIndex++) {
                if (!screenSourceBound(lightIndex)) {
                    continue;
                }

                ScreenSurfaceData lightSurface = screenSurfaces[lightIndex];
                float3 screenNormal = normalize(cross(lightSurface.right.xyz, lightSurface.up.xyz));
                float3 toLight = (lightSurface.origin.xyz - surfacePoint);
                float distanceSquared = max(dot(toLight, toLight), 1e-4);
                float3 lightDirection = (toLight * rsqrt(distanceSquared));
                float facing = (max(dot(normal, lightDirection), 0.0) * saturate(dot(screenNormal, -lightDirection)));
                float attenuation = (1.0 / (1.0 + (ScreenLightFalloff * distanceSquared)));

                radiance += (sdfScreenLights[lightIndex].rgb * ((sdfScreenLights[lightIndex].a * facing) * attenuation));
            }
#endif

            if (material >= SDF_SCREEN_MATERIAL) {
                color = (screenContent(surfacePoint, time) * (0.85 + (0.15 * sunDiffuse)));
            } else {
                color = sdfMaterialShade(sdfMaterialLoad(material), radiance, normal, rayDirection, sunDirection, sunScale);
            }
        }

        if (useFinalShading) {
            float fog = (1.0 - exp(-0.015 * traveled));
            color = lerp(color, skyColor(rayDirection), fog);
        }
    }

    float3 viewColor = color;

    switch (viewMode) {
        case 1: { // depth
            float depth = saturate(traveled / MaxDistance);
            viewColor = float3(depth, depth, depth);
            break;
        }
        case 2: { // surface normals
            viewColor = (hitSurface ? ((normal * 0.5) + 0.5) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 3: { // ray direction
            viewColor = ((rayDirection * 0.5) + 0.5);
            break;
        }
        case 4: { // material id palette
            viewColor = (hitSurface ? materialPalette(material) : float3(0.0, 0.0, 0.0));
            break;
        }
        case 5: { // iteration count ramp (after the cull fast-forward — empty tiles read ~0)
            float ramp = (float(marchStep) / float(MaxSteps));
            viewColor = float3(ramp, ramp, ramp);
            break;
        }
    }

    return viewColor;
}

#endif
