// The counted comparison alternatives. Both replace a normalized set of incoming diffuse samples over the
// residency cache fallback. They read current visibility or the field, never the resolved color/history.
#ifndef SDF_INDIRECT_ALTERNATIVES_HLSLI
#define SDF_INDIRECT_ALTERNATIVES_HLSLI
#include "sdf-indirect-diffuse.hlsli"
#include "sdf-indirect-light.hlsli"
#include "sdf-indirect-screen.hlsli"
#include "../shade/sdf-light.hlsli"
#include "../isa/sdf-sky-kinds.hlsli"
#include "../shade/sdf-sky-lighting.hlsli"

// Comparison bounds are generated from SdfIndirectComparisonLayout.

// Four equal-weight cosine quadrature directions, turned by the existing temporal jitter phase.
float3 sdfIndirectAlternativeDirection(float3 normal, uint ray, uint phase) {
    float3 tangent = normalize(cross(abs(normal.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0), normal));
    float3 bitangent = cross(normal, tangent);
    float angle = ((float)ray * 1.5707963267948966 + (float)phase * 0.3926990816987241);
    float u = ((float)ray + 0.5) / (float)SdfIndirectAlternativeRays;
    return normal * sqrt(1.0 - u) + (tangent * cos(angle) + bitangent * sin(angle)) * sqrt(u);
}

// The explicit outgoing diffuse sources at the sampled hit. Rim, specular, grid, artistic fill and fog never enter.
SdfIndirectSources sdfIndirectAlternativeDiffuse(float3 position, float3 normal, float3 direction, int material,
    float4 shadows, float2 incoming) {
    SdfShadeSurface surface = (SdfShadeSurface)0;
    surface.position = position;
    surface.normal = normal;
    surface.rayDirection = direction;
    surface.material = sdfMaterialLoad(material);
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = shadows;
    surface.incomingVisibility = incoming;
    float attenuation;
    return sdfIndirectDiffuse(surface, attenuation);
}
// A world point projects only into this consumer's current record slice. Points behind its near plane or outside
// the render extent have no screen-space witness, so their directional sample keeps the cache fallback.
bool sdfIndirectAlternativeProject(ViewportData view, float3 position, out uint2 pixel) {
    pixel = 0u;
    float3 relative = position - view.position.xyz;
    float depth = dot(relative, view.forward.xyz);
    if (!isfinite(depth) || depth <= worldSurfaceNearDistance(view)) { return false; }
    float2 tangent = float2(dot(relative, view.right.xyz), dot(relative, view.up.xyz)) / depth;
    float2 ndc = (tangent - view.lens.yz) / float2(view.right.w * view.up.w, view.right.w);
    float2 uv = ndc * float2(0.5, -0.5) + 0.5;
    if (any(!isfinite(uv)) || any(uv < 0.0) || any(uv >= 1.0)) { return false; }
    pixel = (uint2)(uv * (float2)worldViewDims(view));
    return worldVisibilityCurrent(pixel);
}

bool sdfIndirectScreenBounce(SdfPixel p, float3 origin, float3 direction, out SdfIndirectSources sources,
    out bool screenTerminal, inout uint samples) {
    sources = (SdfIndirectSources)0;
    screenTerminal = false;
    float reach = min(SdfIndirectAlternativeReach, p.farDistance);
    float previous = 0.0;
    [loop] for (uint step = 1u; step <= SdfIndirectAlternativeScreenSteps; step++) {
        float fraction = (float)step / (float)SdfIndirectAlternativeScreenSteps;
        float travel = reach * fraction * fraction;
        uint2 pixel;
        if (!sdfIndirectAlternativeProject(p.view, origin + direction * travel, pixel)) { return false; }
        uint record = worldVisibilityRecord(pixel, p.viewIndex);
        SdfSurfaceSample visible = sdfLoadSurfaceSample(record);
        samples++;
        if (!visible.hit) { previous = travel; continue; }
        // Mesh atlas emission has a separate source contract; an unsupported hit keeps fallback.
        if (visible.mesh || visible.material < 0) { return false; }
        float2 uv = ((float2)pixel + 0.5) / (float2)worldViewDims(p.view);
        float3 ray = cameraRayDirection(p.view, uv);
        float3 surfacePoint = cameraRayOrigin(p.view, uv) + ray * visible.t;
        float3 delta = surfacePoint - origin;
        float along = dot(delta, direction);
        float radius = max(0.002, 2.0 * p.pixelFootprint * visible.t);
        if (along <= radius || along < previous - radius || along > travel + radius ||
            length(delta - direction * along) > radius) { previous = travel; continue; }
        if (dot(visible.normal, -direction) <= 0.0) { return false; }
        float3 screenEmission;
        if (sdfIndirectScreenEmission(visible.material, surfacePoint, direction, screenEmission)) {
            sources.values[SdfIndirectSourceScreens] = screenEmission;
            screenTerminal = true;
            return true;
        }
        if (visible.material >= SDF_SCREEN_MATERIAL) { return false; }
        float4 shadows = worldSoftShadowsDisabled() ? 1.0 : sdfLoadVisibilityShadows(record);
        float2 incoming = 1.0;
#if SDF_SHADOW_FADE_SLOTS > 0
        if (!worldSoftShadowsDisabled() && passGroup.shadowFadeCount > 0u) {
#if SDF_SHADOW_FADE_SLOTS == 1
            incoming.x = incomingVisibility.Load(int3(pixel, 0));
#else
            incoming = incomingVisibility.Load(int3(pixel, 0));
#endif
        }
#endif
        sources = sdfIndirectAlternativeDiffuse(surfacePoint, visible.normal, direction, visible.material, shadows, incoming);
        return true;
    }
    return false;
}

// Cone clearance follows the existing full-field ball certificate. A hit still requires absolute acceptance and
// a sign witness; a small conservative distance or an exhausted budget supplies no fabricated bounce. The cone's
// sample and the hit's sign witness are two phases of one field call site, in the original order and allowance.
bool sdfIndirectConeBounce(SdfPixel p, float3 origin, float3 direction, SdfIndirectSources fallback,
    out SdfIndirectSources sources, out bool hitSurface, out bool screenTerminal, inout uint samples) {
    sources = fallback;
    hitSurface = false;
    screenTerminal = false;
    float travel = 0.0;
    float reach = min(SdfIndirectAlternativeReach, p.farDistance);
    float visibility = 1.0;
    uint budget = SdfIndirectAlternativeConeSteps;
    uint step = 0u;
    bool witnessPhase = false;
    float3 position = 0.0;
    float3 normal = 0.0;
    float offset = 0.0;
    SdfHit hit = (SdfHit)0;
    [loop] for (;;) {
        float3 at;
        if (!witnessPhase) {
            if (!(step < SdfIndirectAlternativeConeSteps && budget > 0u)) { break; }
            budget--;
            position = origin + direction * travel;
            at = position;
        } else {
            at = position + normal * offset;
        }
        SdfHit query = sdfIndirectSample(at, SDF_INSTANCE_MASK_ALL);
        samples++;
        if (!witnessPhase) {
            hit = query;
            if (!isfinite(hit.distance) || (travel == 0.0 && hit.distance <= 0.0)) { return false; }
            if (abs(hit.distance) <= SdfIndirectSurfaceEpsilon && budget >= 2u) {
                budget--;
                normal = sdfIndirectGradient(position);
                samples++;
                if (dot(normal, normal) == 0.0 || dot(normal, -direction) <= 0.0) { return false; }
                budget--;
                offset = hit.distance < 0.0 ? SdfIndirectSurfaceEpsilon : -SdfIndirectSurfaceEpsilon;
                witnessPhase = true;
                continue;
            }
        } else {
            SdfHit witness = query;
            bool bracket = isfinite(witness.distance) && (hit.distance < 0.0 ? witness.distance > 0.0 : witness.distance <= 0.0);
            if (!bracket || hit.material < 0) { return false; }
            float3 screenEmission;
            if (sdfIndirectScreenEmission(hit.material, position, direction, screenEmission)) {
                sources = (SdfIndirectSources)0;
                sources.values[SdfIndirectSourceScreens] = screenEmission;
                hitSurface = true;
                screenTerminal = true;
                return true;
            }
            if (hit.material >= SDF_SCREEN_MATERIAL) { return false; }
            float4 shadows;
            float2 incoming;
            sdfIndirectDiffuseVisibilities(position, normal, shadows, incoming);
            // A surface fills the cone that reaches it. Replace its incoming sample with the one diffuse bounce.
            sources = sdfIndirectAlternativeDiffuse(position, normal, direction, hit.material, shadows, incoming);
            hitSurface = true;
            return true;
        }
        float clearance = sdfMapBallClearance(hit.distance);
        if (!isfinite(clearance) || clearance <= 0.0) { return false; }
        if (travel > 0.0) { visibility = min(visibility, saturate(clearance / max(travel * SdfIndirectAlternativeConeSlope, 0.001))); }
        if (clearance > reach - travel) {
            [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) { sources.values[source] *= visibility; }
            // A local interval is not a sky exit. Keep its certified cache sky, including sealed-room darkness.
            sources.values[SdfIndirectSourceSky] = fallback.values[SdfIndirectSourceSky];
            if ((passGroup.indirectSources & SdfIndirectSourcesSky) != 0u && clearance > p.farDistance - travel) {
                sources.values[SdfIndirectSourceSky] = sdfSkyPhysicalRadiance(direction) * (visibility * passGroup.indirectSourceGains.w);
            }
            return true;
        }
        float advance = min(clearance, reach - travel);
        if (advance <= 0.0) { return false; }
        travel += advance;
        step++;
    }
    return false;
}

// Preserve this shared comparison body in SPIR-V: expanding it into Views overflows or crashes legalization.
// DXIL retains ordinary inlining because its validator rejects vector values in retained helper functions.
#ifdef __spirv__
[noinline]
#endif
SdfIndirectSources sdfIndirectAlternative(SdfPixel p, SdfSurfaceSample receiver, float3 launched, SdfIndirectSources fallback) {
    if (passGroup.indirectMethod == SdfIndirectMethodCache || passGroup.indirectTier == SdfIndirectTierOff) { return fallback; }
    uint phase = passGroup.historyFrames % SdfIndirectAlternativePhases;
    if (((p.pixel.x & 1u) | ((p.pixel.y & 1u) << 1u)) != phase) { return fallback; }
    SdfIndirectSources total = (SdfIndirectSources)0;
    uint hits = 0u;
    uint samples = 0u;
    uint unresolved = 0u;
    bool previousSecondary = sdfSecondaryMarchActive;
    bool previousShadow = sdfShadowParticipationActive;
    sdfSecondaryMarchActive = true;
    sdfShadowParticipationActive = true;
    // Keep one bounded ray body: each cone includes the complete field and directional-shadow helpers.
    [loop] for (uint ray = 0u; ray < SdfIndirectAlternativeRays; ray++) {
        float3 direction = sdfIndirectAlternativeDirection(receiver.normal, ray, phase);
        SdfIndirectSources incoming;
        bool hitSurface = false;
        bool screenTerminal;
        bool answered;
        if (passGroup.indirectMethod == SdfIndirectMethodScreen) {
            answered = sdfIndirectScreenBounce(p, launched, direction, incoming, screenTerminal, samples);
            hitSurface = answered;
        } else {
            answered = sdfIndirectConeBounce(p, launched, direction, fallback, incoming, hitSurface, screenTerminal, samples);
        }
        if (!answered) { incoming = fallback; unresolved++; }
        else if (hitSurface) {
            hits++;
            // A secondary surface has no certified sky hemisphere. The cache owns this component until a
            // visibility-weighted secondary integral replaces it; raw harmonic ambient would leak through walls.
            // A screen is an emission terminal, including a valid dark answer; no cached sky passes through it.
            if (!screenTerminal) { incoming.values[SdfIndirectSourceSky] = fallback.values[SdfIndirectSourceSky]; }
        }
        [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) { total.values[source] += incoming.values[source] / (float)SdfIndirectAlternativeRays; }
    }
    sdfShadowParticipationActive = previousShadow;
    sdfSecondaryMarchActive = previousSecondary;
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, hits, samples, unresolved);
    return total;
}
#endif
