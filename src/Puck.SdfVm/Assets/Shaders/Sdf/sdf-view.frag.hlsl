// One viewport's SDF raymarch (single-source HLSL; DXC compiles it to SPIR-V for Vulkan and DXIL for DirectX).
// The whole picture is a function of the 80-byte camera basis plus the program in the storage buffer.
// KEEP IN SYNC with Puck.SdfVm.Rendering.SdfViewRenderer's push-constant packing.
#include "sdf-vm.hlsli"

// Camera basis. On Vulkan this is the push-constant block; on DirectX it is the root-constant cbuffer at b0.
// Same 80-byte layout / field order as the host's packing.
struct CameraData {
    float4 position;    // xyz = world position, w = time (seconds)
    float4 right;       // xyz = right basis,   w = tan(fov / 2)
    float4 up;          // xyz = up basis,      w = aspect ratio
    float4 forward;     // xyz = forward basis, w = debug view mode (0 = final; host writes 0 by default)
    float4 resolution;  // xy = framebuffer size in pixels
};
[[vk::push_constant]] ConstantBuffer<CameraData> camera;

static const int MaxSteps = 160;
static const float MaxDistance = 60.0;
static const float SurfaceEpsilon = 0.001;

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
// A distinct, stable hue per material id (an HSV hue ramp), not the table albedo — so id boundaries read
// clearly in the material-id debug view.
float3 materialPalette(int material) {
    float hue = frac(float(material) * 0.61803399);
    float3 ramp = (abs((frac(hue + float3(0.0, 0.33333333, 0.66666667)) * 6.0) - 3.0) - 1.0);

    return saturate(ramp);
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    float2 ndc = (((fragCoord.xy / camera.resolution.xy) * 2.0) - 1.0);

    // SV_Position origin is top-left (y down); flip so screen-up maps to the camera's +up.
    ndc.y = -ndc.y;

    // forward.w carries the debug view mode (0 = final image); the ray basis only uses forward.xyz.
    int viewMode = (int)round(camera.forward.w);
    float tanHalfFov = camera.right.w;
    float aspect = camera.up.w;
    float3 rayOrigin = camera.position.xyz;
    float3 rayDirection = normalize(
        camera.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * camera.right.xyz) +
        ((ndc.y * tanHalfFov) * camera.up.xyz)
    );

    float traveled = 0.0;
    bool hitSurface = false;
    int material = 0;
    int marchStep = 0;

    // Lifted out of the for-header so the iteration count survives the loop for the debug view.
    [loop]
    for (marchStep = 0; (marchStep < MaxSteps); marchStep++) {
        float3 samplePoint = (rayOrigin + (rayDirection * traveled));
        SdfHit hit = map(samplePoint);

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

    // Lifted so the normals view can read it; only computed on a hit (otherwise zero).
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
            color = (screenContent(surfacePoint, camera.position.w) * (0.85 + (0.15 * diffuse)));
        } else {
            float3 albedo = sdfMaterialAlbedo(material);
            color = (albedo * (ambient + (0.85 * diffuse)));
        }

        float fog = (1.0 - exp(-0.015 * traveled));
        color = lerp(color, skyColor(rayDirection), fog);
    }

    // The debug views read the lifted march outputs; mode 0 is the untouched final image.
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
        case 5: { // iteration count ramp
            float ramp = (float(marchStep) / float(MaxSteps));
            viewColor = float3(ramp, ramp, ramp);
            break;
        }
    }

    return float4(viewColor, 1.0);
}
