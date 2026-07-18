// Ray-query world kernel — the Vulkan/Direct3D 12 inline ray-tracing path. It RAY-TRACES the cull, then SDF-MARCHES
// the surface: an inline RayQuery against the world TLAS (one unit-AABB instance per finite SDF primitive) gives each
// pixel the nearest candidate primitive's bound, which — combined with an analytic ground-plane crossing — sets a
// tight march-start so the sphere march of the full map() skips the empty space ahead of it. Rays that hit neither a
// primitive bound nor the floor are sky (the cull). The march still evaluates the COMPLETE map() (plane + every
// primitive + smooth blends), so the surface and shading stay comparable to the beam-path --world; the TLAS only
// accelerates WHERE the march runs, it never changes WHAT is drawn.
//
// BOTH BACKENDS at Shader Model 6.5: SPIR-V (Vulkan ray-query) and DXIL (Direct3D 12 DXR 1.1 inline ray tracing). The
// BLAS is a single unit AABB [-1,1]^3 (procedural geometry), so traversal surfaces CANDIDATE_PROCEDURAL_PRIMITIVE and
// the kernel does its own ray/box intersection and commits the hit — there is no fixed-function AABB intersector.
#include "sdf-vm.hlsli"

// The output image (binding 0 / u0). sdf-vm.hlsli binds the scene program (binding 1 / t0). The TLAS is a fresh
// binding that does not collide with the program: binding 2 / t1 (the neutral pipeline factory assigns the program
// SRV t0 then the acceleration-structure SRV t1, in binding order, on Direct3D 12).
[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> outputImage : register(u0);
[[vk::binding(2, 0)]] RaytracingAccelerationStructure worldTlas : register(t1);

struct RtParams {
    uint2 imageExtent;      // output image size in pixels
    uint2 reserved;         // padding to align the camera vectors to 16 bytes
    float4 cameraPosition;  // xyz = world eye position
    float4 cameraRight;     // xyz = right basis,   w = tan(fov / 2)
    float4 cameraUp;        // xyz = up basis,      w = aspect ratio
    float4 cameraForward;   // xyz = forward basis
    float4 groundPlane;     // xyz = plane normal,  w = plane offset (the scene's infinite floor; xyz = 0 if none)
};
[[vk::push_constant]] ConstantBuffer<RtParams> params;

static const int MaxSteps = 160;
static const float MaxDistance = 60.0;
static const float SurfaceEpsilon = 0.001;
static const float CullSkin = 0.5;          // pull the march start back by the max smooth-blend bulge, for safety
// The "this ray hit nothing" sentinel traceInstanceEntry / traceGroundPlane return. It MUST exceed MaxDistance: every
// caller decides "culled" by testing the returned entry against MaxDistance (or ShadowMaxDistance, which is smaller).
static const float NoRayHit = (MaxDistance * 2.0);
// Degenerate-ground-plane guards. The scene declares "no floor" as an all-zero plane normal, so dot(n, n) below this
// floor means no plane at all; and a ray whose dot(direction, n) is not at least this far NEGATIVE is parallel to, or
// receding from, the plane's front face and never crosses it.
static const float GroundPlaneNormalLengthSquaredMin = 1.0e-6;
static const float GroundPlaneApproachMax = -1.0e-5;
static const int ShadowSteps = 48;
static const float ShadowMaxDistance = 24.0;
static const float ShadowBias = 0.02;
static const float ShadowSharpness = 9.0;
static const float ShadowAmbient = 0.30;    // residual light in shadow (ambient term keeps shadows from going black)
// The soft-shadow march's per-step advance clamp — MIRRORS sdf-world.hlsli's ShadowStepMin / ShadowStepMax (same values,
// same semantics: the floor keeps a near-tangent march from stalling, the ceiling keeps the penumbra from
// over-marching a grazing silhouette). Declared locally because this ray-query kernel cannot include sdf-world.hlsli.
static const float ShadowStepMin = 0.02;
static const float ShadowStepMax = 0.6;
// The gradient probe's finite-difference offset.
static const float NormalProbeEpsilon = 0.0006;

// A 6-tap central difference — deliberately NOT the world path's 4-tap tetrahedron probe. The two disagree by a few
// LSB, and RtStage's cross-backend parity budget is measured against THIS probe; migrating it would move every pixel.
float3 calculateNormal(float3 p) {
    float2 e = float2(NormalProbeEpsilon, 0.0);

    return normalize(float3(
        (mapDistance(p + e.xyy) - mapDistance(p - e.xyy)),
        (mapDistance(p + e.yxy) - mapDistance(p - e.yxy)),
        (mapDistance(p + e.yyx) - mapDistance(p - e.yyx))
    ));
}
// A DELIBERATELY brighter, higher-contrast gradient than sdf-world.hlsli's sky (0.04,0.05,0.07 -> 0.10,0.13,0.20): this
// is a diagnostic-only palette that makes the ray-traced cull's sky/geometry boundary legible. Not drift — do not
// "reconcile" it with the world path.
static const float3 SkyHorizonColor = float3(0.02, 0.03, 0.06);
static const float3 SkyZenithColor = float3(0.20, 0.30, 0.45);

float3 skyColor(float3 direction) {
    float t = clamp((0.5 * (direction.y + 1.0)), 0.0, 1.0);

    return lerp(SkyHorizonColor, SkyZenithColor, t);
}

// Nearest entry distance of any TLAS instance the ray pierces, or a large value on a miss. The BLAS is procedural, so
// candidates are intersected by hand (slab test of the unit box in the instance's object space) and the nearest entry
// committed via CommitProceduralPrimitiveHit.
float traceInstanceEntry(float3 rayOrigin, float3 rayDirection, float tMin, float tMax) {
    RayDesc ray;

    ray.Origin = rayOrigin;
    ray.Direction = rayDirection;
    ray.TMin = tMin;
    ray.TMax = tMax;

    RayQuery<RAY_FLAG_NONE> query;

    query.TraceRayInline(worldTlas, RAY_FLAG_NONE, 0xFFu, ray);

    while (query.Proceed()) {
        if (query.CandidateType() == CANDIDATE_PROCEDURAL_PRIMITIVE) {
            float3 objectOrigin = query.CandidateObjectRayOrigin();
            float3 objectDirection = query.CandidateObjectRayDirection();
            float3 inverseDirection = (1.0 / objectDirection);
            float3 tLower = ((float3(-1.0, -1.0, -1.0) - objectOrigin) * inverseDirection);
            float3 tUpper = ((float3(1.0, 1.0, 1.0) - objectOrigin) * inverseDirection);
            float3 tSmaller = min(tLower, tUpper);
            float3 tLarger = max(tLower, tUpper);
            float tNear = max(max(tSmaller.x, tSmaller.y), tSmaller.z);
            float tFar = min(min(tLarger.x, tLarger.y), tLarger.z);
            float tHit = ((tNear >= ray.TMin) ? tNear : tFar);

            if ((tNear <= tFar) && (tHit >= ray.TMin) && (tHit <= query.CommittedRayT())) {
                query.CommitProceduralPrimitiveHit(tHit);
            }
        }
    }

    return ((query.CommittedStatus() == COMMITTED_PROCEDURAL_PRIMITIVE_HIT) ? query.CommittedRayT() : NoRayHit);
}

// The ray's crossing of the scene's infinite ground plane (dot(p, n) + offset = 0), or a large value when the ray
// recedes from it (or there is no plane). map() still evaluates the plane; this only contributes a march-start bound.
float traceGroundPlane(float3 rayOrigin, float3 rayDirection) {
    float3 normal = params.groundPlane.xyz;

    if (dot(normal, normal) < GroundPlaneNormalLengthSquaredMin) {
        return NoRayHit; // no floor in this scene
    }

    float denominator = dot(rayDirection, normal);

    if (denominator > GroundPlaneApproachMax) {
        return NoRayHit; // parallel to, or receding from, the plane front
    }

    return (-(dot(rayOrigin, normal) + params.groundPlane.w) / denominator);
}

// RT-accelerated soft shadow toward the (directional) light. The TLAS answers "could anything occlude this point?":
// if the shadow ray pierces no instance bound, the point is fully lit and the occlusion march is SKIPPED (the cull);
// otherwise a soft-shadow march of map() runs, fast-forwarded to the occluder bound. The light is above, so
// the infinite floor never occludes an upward shadow ray and is left out of the test.
float lightShadow(float3 surfacePoint, float3 surfaceNormal, float3 lightDirection) {
    float3 origin = (surfacePoint + (surfaceNormal * ShadowBias));
    float occluderEntry = traceInstanceEntry(origin, lightDirection, ShadowBias, ShadowMaxDistance);

    if (occluderEntry > ShadowMaxDistance) {
        return 1.0; // no occluder bound on the way to the light — fully lit, no march
    }

    float result = 1.0;
    float traveled = max(ShadowBias, (occluderEntry - CullSkin));

    [loop]
    for (int step = 0; (step < ShadowSteps); step++) {
        // `clearance`, not `distance`: a local named `distance` shadows the HLSL distance() intrinsic.
        float clearance = mapDistance(origin + (lightDirection * traveled));

        if (clearance < SurfaceEpsilon) {
            return 0.0; // hard occlusion
        }

        result = min(result, ((ShadowSharpness * clearance) / traveled));
        traveled += clamp(clearance, ShadowStepMin, ShadowStepMax);

        if (traveled > ShadowMaxDistance) {
            break;
        }
    }

    return saturate(result);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (
        (id.x >= params.imageExtent.x) ||
        (id.y >= params.imageExtent.y)
    ) {
        return;
    }

    // Primary ray from the real camera — identical construction to the SDF kernels' cameraRayDirection.
    float2 localUv = ((float2(id.xy) + 0.5) / float2(params.imageExtent));
    float2 ndc = ((localUv * 2.0) - 1.0);

    ndc.y = -ndc.y;

    float tanHalfFov = params.cameraRight.w;
    float aspect = params.cameraUp.w;
    float3 rayOrigin = params.cameraPosition.xyz;

    // The symmetry-LOD origin: the camera; shadow rays inherit it, so an instance's LOD level stays self-consistent.
    sdfLodOrigin = rayOrigin;
    // The per-invocation program-layout cache (sdf-vm.hlsli) — the primary march (<=MaxSteps), lightShadow's march
    // (<=ShadowSteps), and calculateNormal's taps all call map()/mapDistance() per step, so the decode must happen
    // exactly once here, before any of them run.
    sdfProgramLayout = sdfLoadProgramLayout();
    float3 rayDirection = normalize(
        params.cameraForward.xyz +
        (((ndc.x * aspect) * tanHalfFov) * params.cameraRight.xyz) +
        ((ndc.y * tanHalfFov) * params.cameraUp.xyz)
    );

    // Ray-traced cull: the nearest of the candidate primitive bound and the analytic floor crossing sets the march
    // start; if neither is within range the pixel is sky and the march is skipped entirely.
    float instanceEntry = traceInstanceEntry(rayOrigin, rayDirection, SurfaceEpsilon, MaxDistance);
    float floorEntry = traceGroundPlane(rayOrigin, rayDirection);
    float candidate = min(instanceEntry, floorEntry);

    float3 color;

    if (candidate > MaxDistance) {
        color = skyColor(rayDirection); // culled: provably hits nothing in range
    } else {
        float traveled = max(SurfaceEpsilon, (candidate - CullSkin));
        bool hitSurface = false;
        // The hit's material, CARRIED OUT of the march. The loop already evaluated map() at the surface point; a second
        // map(position) below purely to recover .material would re-run the whole program (~10% of this kernel's code).
        int hitMaterial = 0;

        [loop]
        for (int step = 0; (step < MaxSteps); step++) {
            float3 position = (rayOrigin + (rayDirection * traveled));
            SdfHit hit = map(position);

            if (hit.distance < SurfaceEpsilon) {
                hitSurface = true;
                hitMaterial = hit.material;
                break;
            }

            traveled += hit.distance;

            if (traveled > MaxDistance) {
                break;
            }
        }

        if (hitSurface) {
            // `traveled` is unchanged since the breaking iteration, so this reproduces that iteration's point exactly.
            float3 position = (rayOrigin + (rayDirection * traveled));
            float3 normal = calculateNormal(position);
            // RT shadow: the second TLAS query of the frame, accelerating the occlusion test.
            float shadow = lerp(ShadowAmbient, 1.0, lightShadow(position, normal, SdfSunDirection));
            float diffuse = (max(0.0, dot(normal, SdfSunDirection)) * shadow);

            if (hitMaterial >= SDF_SCREEN_MATERIAL) {
                // A screen sentinel (SDF_SCREEN_MATERIAL and up) names no row of the material table — sdfMaterialLoad
                // would index past its end. This DIAGNOSTIC kernel has no screen sources bound and must not invent
                // screen shading, so a screen face reads black. That is exactly what an unguarded out-of-bounds
                // structured-buffer read already yields on Direct3D 12 (zeroed), while on Vulkan the same read is only
                // defined under robustBufferAccess — so the guard makes both backends agree by CONSTRUCTION rather than
                // by driver luck, and moves no pixel of any program that declares no ScreenSlab.
                color = float3(0.0, 0.0, 0.0);
            } else {
                // The highlight attenuates by the same RT shadow term as the diffuse (a highlight never glows in shadow).
                color = sdfMaterialShade(sdfMaterialLoad(hitMaterial), (float3(1.0, 1.0, 1.0) * (0.25 + (0.75 * diffuse))), normal, rayDirection, SdfSunDirection, shadow);
            }
        } else {
            color = skyColor(rayDirection);
        }
    }

    // No sdfR2Dither here, deliberately: the world path dithers before its 8-bit store to break sky/fog gradient
    // banding, but this kernel is a cross-backend PARITY probe — the store must stay a pure function of the shaded
    // color, with no pixel-coordinate-keyed noise folded in.
    outputImage[id.xy] = float4(color, 1.0);
}
