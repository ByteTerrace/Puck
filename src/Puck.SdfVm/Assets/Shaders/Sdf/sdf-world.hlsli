// Shared data contract + shading for the SDF world compositor compute kernels: sdf-beam.comp (the tile-cull
// prepass) and sdf-world-views.comp (Stage 1, per-view rendering; sdf-world-composite.comp does Stage 2). Both run
// the generic VM (sdf-vm.hlsli) over the scene program and a table of viewports/cameras supplied as DATA. KEEP IN
// SYNC with WorldProducerNode's packing and with sdf-view.frag.hlsl's shading.
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
};
[[vk::push_constant]] ConstantBuffer<CompositeParams> params;

// Whether viewport v is a hosted child surface (its source[] slot holds another node's output): the beam prepass
// and Stage 1 skip such slots so the SDF render never overwrites the child's pixels.
bool isChildViewport(uint viewportIndex) {
    return (0u != (params.childMask & (1u << viewportIndex)));
}

static const int MaxSteps = 160;
static const float MaxDistance = 60.0;
static const float SurfaceEpsilon = 0.001;
static const uint WorldTileSize = 16u;
static const int ConeMarchSteps = 96;
static const float ConeNear = 0.02;
static const float ConeEpsilon = 0.002;
static const float TileEmpty = -1.0; // sentinel: the cone clears the field — no ray in the tile can hit

float3 calculateNormal(float3 p) {
    float2 e = float2(0.0006, 0.0);

    return normalize(float3(
        (map(p + e.xyy).distance - map(p - e.xyy).distance),
        (map(p + e.yxy).distance - map(p - e.yxy).distance),
        (map(p + e.yyx).distance - map(p - e.yyx).distance)
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

// Conservative cone (beam) march over the distance field for a tile's UV rect. map() is a true distance field, so
// the cone of half-spread `chord` clears ALL of its rays while map(center) - chord*t > 0, and a 1-Lipschitz-safe
// step is clearance / (1 + chord). Returns the earliest t at which any ray in the tile could hit — a march-start
// lower bound shared by every pixel in the tile — or TileEmpty when the cone clears the field out to MaxDistance.
float coneMarchTile(ViewportData view, float2 localUvMin, float2 localUvMax) {
    float3 origin = view.position.xyz;
    float3 centerDirection = cameraRayDirection(view, (0.5 * (localUvMin + localUvMax)));
    float chord = 0.0;

    chord = max(chord, length(cameraRayDirection(view, localUvMin) - centerDirection));
    chord = max(chord, length(cameraRayDirection(view, float2(localUvMax.x, localUvMin.y)) - centerDirection));
    chord = max(chord, length(cameraRayDirection(view, float2(localUvMin.x, localUvMax.y)) - centerDirection));
    chord = max(chord, length(cameraRayDirection(view, localUvMax) - centerDirection));

    float t = ConeNear;

    [loop]
    for (int i = 0; (i < ConeMarchSteps); i++) {
        float clearance = (map(origin + (centerDirection * t)).distance - (chord * t));

        if (clearance <= ConeEpsilon) {
            return t;
        }

        t += (clearance / (1.0 + chord));

        if (t > MaxDistance) {
            return TileEmpty;
        }
    }

    return t;
}

// March + shade one viewport's ray for a pixel at the viewport-local UV, starting the march at `marchStart` (the
// tile-cull lower bound; TileEmpty skips the march entirely → background) and resolving the debug view mode.
float3 renderView(ViewportData view, float2 localUv, float marchStart) {
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
            SdfHit hit = map(rayOrigin + (rayDirection * traveled));

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

    if (hitSurface) {
        normal = calculateNormal(rayOrigin + (rayDirection * traveled));
    }

    float3 color = skyColor(rayDirection);

    if (hitSurface) {
        float3 surfacePoint = (rayOrigin + (rayDirection * traveled));
        float3 lightDirection = normalize(float3(0.55, 0.85, 0.35));
        float diffuse = max(dot(normal, lightDirection), 0.0);
        float ambient = (0.25 + (0.25 * normal.y));

        if (material == SDF_SCREEN_MATERIAL) {
            color = (screenContent(surfacePoint, time) * (0.85 + (0.15 * diffuse)));
        } else {
            float3 albedo = sdfMaterialAlbedo(material);
            color = (albedo * (ambient + (0.85 * diffuse)));
        }

        float fog = (1.0 - exp(-0.015 * traveled));
        color = lerp(color, skyColor(rayDirection), fog);
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
