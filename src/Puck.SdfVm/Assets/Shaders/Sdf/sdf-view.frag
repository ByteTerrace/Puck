#version 450

#include "sdf-vm.glsl"

// One viewport's camera, resolved to a primary-ray basis by the host each frame. The whole picture
// is a function of this 80-byte push constant plus the program in the storage buffer — "viewport as
// data". KEEP IN SYNC with Puck.SdfVm.Rendering.SdfViewRenderer's push-constant packing.
layout(push_constant) uniform CameraPushConstants {
    vec4 position;    // xyz = world position, w = time (seconds)
    vec4 right;       // xyz = right basis,   w = tan(fov / 2)
    vec4 up;          // xyz = up basis,      w = aspect ratio
    vec4 forward;     // xyz = forward basis, w = (reserved)
    vec4 resolution;  // xy = framebuffer size in pixels
} camera;

layout(location = 0) out vec4 outColor;

const int MaxSteps = 160;
const float MaxDistance = 60.0;
const float SurfaceEpsilon = 0.001;

vec3 calculateNormal(vec3 p) {
    vec2 e = vec2(0.0006, 0.0);

    return normalize(vec3(
        (map(p + e.xyy).distance - map(p - e.xyy).distance),
        (map(p + e.yxy).distance - map(p - e.yxy).distance),
        (map(p + e.yyx).distance - map(p - e.yyx).distance)
    ));
}
// Procedural placeholder for a SCREEN_SLAB face: an animated test-card so the in-world screen reads
// as a screen until the jumbotron phase feeds it a real viewport texture.
vec3 screenContent(vec3 p, float time) {
    float bars = (0.5 + (0.5 * sin((p.y * 26.0) - (time * 5.0))));
    vec3 baseColor = mix(vec3(0.02, 0.04, 0.09), vec3(0.10, 0.80, 1.00), bars);
    float sweep = smoothstep(0.49, 0.5, fract((p.x * 1.3) + (time * 0.4)));

    return (baseColor + (0.35 * vec3(0.95, 0.45, 0.12) * sweep));
}
vec3 skyColor(vec3 direction) {
    float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

    return mix(vec3(0.04, 0.05, 0.07), vec3(0.10, 0.13, 0.20), t);
}

void main() {
    vec2 ndc = (((gl_FragCoord.xy / camera.resolution.xy) * 2.0) - 1.0);

    // gl_FragCoord origin is top-left (y down); flip so screen-up maps to the camera's +up.
    ndc.y = -ndc.y;

    float tanHalfFov = camera.right.w;
    float aspect = camera.up.w;
    vec3 rayOrigin = camera.position.xyz;
    vec3 rayDirection = normalize(
        camera.forward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * camera.right.xyz) +
        ((ndc.y * tanHalfFov) * camera.up.xyz)
    );

    float traveled = 0.0;
    bool hitSurface = false;
    int material = 0;

    for (int step = 0; (step < MaxSteps); step++) {
        vec3 samplePoint = (rayOrigin + (rayDirection * traveled));
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

    vec3 color = skyColor(rayDirection);

    if (hitSurface) {
        vec3 surfacePoint = (rayOrigin + (rayDirection * traveled));
        vec3 normal = calculateNormal(surfacePoint);
        vec3 lightDirection = normalize(vec3(0.55, 0.85, 0.35));
        float diffuse = max(dot(normal, lightDirection), 0.0);
        float ambient = (0.25 + (0.25 * normal.y));

        if (material == SDF_SCREEN_MATERIAL) {
            // Emissive screen: show its content directly, lightly shaded so it still sits in the scene.
            color = (screenContent(surfacePoint, camera.position.w) * (0.85 + (0.15 * diffuse)));
        } else {
            vec3 albedo = sdfMaterialAlbedo(material);
            color = (albedo * (ambient + (0.85 * diffuse)));
        }

        // Distance fog into the sky, so the infinite ground plane fades instead of banding.
        float fog = (1.0 - exp(-0.015 * traveled));
        color = mix(color, skyColor(rayDirection), fog);
    }

    outColor = vec4(color, 1.0);
}
