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
            incoming = incomingVisibility.Load(int3(pixel, 0));
        }
#endif
        sources = sdfIndirectAlternativeDiffuse(surfacePoint, visible.normal, direction, visible.material, shadows, incoming);
        return true;
    }
    return false;
}

// Cone clearance follows the existing full-field ball certificate. A hit still requires absolute acceptance and
// a sign witness; a small conservative distance or an exhausted budget supplies no fabricated bounce. The cone's
// sample, the hit's gradient and its sign witness are the queries of one procedure (sdfIndirectConeBounceStep), in the
// original order and allowance, and a hit's diffuse visibilities its one call.
struct SdfIndirectConeBounceProc {
    float3 origin;
    float3 direction;
    SdfIndirectSources fallback;
    float farDistance;
    SdfIndirectSources sources;
    bool hitSurface;
    bool screenTerminal;
    bool answered;
    uint samples;
    float travel;
    float reach;
    float visibility;
    uint budget;
    uint step;
    bool witnessPhase;
    float3 position;
    float3 normal;
    float offset;
    SdfHit hit;
    uint resume;
};
static SdfIndirectConeBounceProc sdfIndirectConeBounceProc = (SdfIndirectConeBounceProc)0;

static const uint SdfIndirectConeBegun = 0u;
static const uint SdfIndirectConeSampled = 1u;
static const uint SdfIndirectConeGradient = 2u;
static const uint SdfIndirectConeShadowed = 3u;

uint sdfIndirectConeBounceBegin(float3 origin, float3 direction, SdfIndirectSources fallback, float farDistance, uint samples) {
    sdfIndirectConeBounceProc.origin = origin;
    sdfIndirectConeBounceProc.direction = direction;
    sdfIndirectConeBounceProc.fallback = fallback;
    sdfIndirectConeBounceProc.farDistance = farDistance;
    sdfIndirectConeBounceProc.samples = samples;
    sdfIndirectConeBounceProc.resume = SdfIndirectConeBegun;
    return SdfIndirectProcConeBounce;
}

uint sdfIndirectConeBounceStep() {
    float3 direction = sdfIndirectConeBounceProc.direction;
    uint resume = sdfIndirectConeBounceProc.resume;
    if (resume == SdfIndirectConeBegun) {
        sdfIndirectConeBounceProc.sources = sdfIndirectConeBounceProc.fallback;
        sdfIndirectConeBounceProc.hitSurface = false;
        sdfIndirectConeBounceProc.screenTerminal = false;
        sdfIndirectConeBounceProc.answered = false;
        sdfIndirectConeBounceProc.travel = 0.0;
        sdfIndirectConeBounceProc.reach = min(SdfIndirectAlternativeReach, sdfIndirectConeBounceProc.farDistance);
        sdfIndirectConeBounceProc.visibility = 1.0;
        sdfIndirectConeBounceProc.budget = SdfIndirectAlternativeConeSteps;
        sdfIndirectConeBounceProc.step = 0u;
        sdfIndirectConeBounceProc.witnessPhase = false;
        sdfIndirectConeBounceProc.position = 0.0;
        sdfIndirectConeBounceProc.normal = 0.0;
        sdfIndirectConeBounceProc.offset = 0.0;
        sdfIndirectConeBounceProc.hit = (SdfHit)0;
    } else if (resume == SdfIndirectConeGradient) {
        sdfIndirectConeBounceProc.normal = sdfIndirectReplyGradient;
        sdfIndirectConeBounceProc.samples++;
        float3 normal = sdfIndirectConeBounceProc.normal;
        if (dot(normal, normal) == 0.0 || dot(normal, -direction) <= 0.0) { return SdfIndirectStepReturn; }
        sdfIndirectConeBounceProc.budget--;
        sdfIndirectConeBounceProc.offset = sdfIndirectConeBounceProc.hit.distance < 0.0 ? SdfIndirectSurfaceEpsilon : -SdfIndirectSurfaceEpsilon;
        sdfIndirectConeBounceProc.witnessPhase = true;
    } else if (resume == SdfIndirectConeShadowed) {
        // A surface fills the cone that reaches it. Replace its incoming sample with the one diffuse bounce.
        sdfIndirectConeBounceProc.sources = sdfIndirectAlternativeDiffuse(sdfIndirectConeBounceProc.position, sdfIndirectConeBounceProc.normal, direction,
            sdfIndirectConeBounceProc.hit.material, sdfIndirectVisibilitiesProc.shadows, sdfIndirectVisibilitiesProc.incoming);
        sdfIndirectConeBounceProc.hitSurface = true;
        sdfIndirectConeBounceProc.answered = true;
        return SdfIndirectStepReturn;
    } else {
        SdfHit query = sdfIndirectReply;
        sdfIndirectConeBounceProc.samples++;
        if (!sdfIndirectConeBounceProc.witnessPhase) {
            sdfIndirectConeBounceProc.hit = query;
            SdfHit hit = query;
            float travel = sdfIndirectConeBounceProc.travel;
            if (!isfinite(hit.distance) || (travel == 0.0 && hit.distance <= 0.0)) { return SdfIndirectStepReturn; }
            if (abs(hit.distance) <= SdfIndirectSurfaceEpsilon && sdfIndirectConeBounceProc.budget >= 2u) {
                sdfIndirectConeBounceProc.budget--;
                sdfIndirectConeBounceProc.resume = SdfIndirectConeGradient;
                return sdfIndirectAskGradient(sdfIndirectConeBounceProc.position);
            }
            float reach = sdfIndirectConeBounceProc.reach;
            float clearance = sdfMapBallClearance(hit.distance);
            if (!isfinite(clearance) || clearance <= 0.0) { return SdfIndirectStepReturn; }
            if (travel > 0.0) { sdfIndirectConeBounceProc.visibility = min(sdfIndirectConeBounceProc.visibility, saturate(clearance / max(travel * SdfIndirectAlternativeConeSlope, 0.001))); }
            if (clearance > reach - travel) {
                float visibility = sdfIndirectConeBounceProc.visibility;
                [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) { sdfIndirectConeBounceProc.sources.values[source] *= visibility; }
                // A local interval is not a sky exit. Keep its certified cache sky, including sealed-room darkness.
                sdfIndirectConeBounceProc.sources.values[SdfIndirectSourceSky] = sdfIndirectConeBounceProc.fallback.values[SdfIndirectSourceSky];
                if ((passGroup.indirectSources & SdfIndirectSourcesSky) != 0u && clearance > sdfIndirectConeBounceProc.farDistance - travel) {
                    sdfIndirectConeBounceProc.sources.values[SdfIndirectSourceSky] = sdfSkyPhysicalRadiance(direction) * (visibility * passGroup.indirectSourceGains.w);
                }
                sdfIndirectConeBounceProc.answered = true;
                return SdfIndirectStepReturn;
            }
            float advance = min(clearance, reach - travel);
            if (advance <= 0.0) { return SdfIndirectStepReturn; }
            sdfIndirectConeBounceProc.travel += advance;
            sdfIndirectConeBounceProc.step++;
        } else {
            SdfHit witness = query;
            SdfHit hit = sdfIndirectConeBounceProc.hit;
            bool bracket = isfinite(witness.distance) && (hit.distance < 0.0 ? witness.distance > 0.0 : witness.distance <= 0.0);
            if (!bracket || hit.material < 0) { return SdfIndirectStepReturn; }
            float3 screenEmission;
            if (sdfIndirectScreenEmission(hit.material, sdfIndirectConeBounceProc.position, direction, screenEmission)) {
                sdfIndirectConeBounceProc.sources = (SdfIndirectSources)0;
                sdfIndirectConeBounceProc.sources.values[SdfIndirectSourceScreens] = screenEmission;
                sdfIndirectConeBounceProc.hitSurface = true;
                sdfIndirectConeBounceProc.screenTerminal = true;
                sdfIndirectConeBounceProc.answered = true;
                return SdfIndirectStepReturn;
            }
            if (hit.material >= SDF_SCREEN_MATERIAL) { return SdfIndirectStepReturn; }
            sdfIndirectConeBounceProc.resume = SdfIndirectConeShadowed;
            return sdfIndirectCall(sdfIndirectVisibilitiesBegin(sdfIndirectConeBounceProc.position, sdfIndirectConeBounceProc.normal));
        }
    }
    float3 at;
    if (!sdfIndirectConeBounceProc.witnessPhase) {
        if (!(sdfIndirectConeBounceProc.step < SdfIndirectAlternativeConeSteps && sdfIndirectConeBounceProc.budget > 0u)) { return SdfIndirectStepReturn; }
        sdfIndirectConeBounceProc.budget--;
        sdfIndirectConeBounceProc.position = sdfIndirectConeBounceProc.origin + direction * sdfIndirectConeBounceProc.travel;
        at = sdfIndirectConeBounceProc.position;
    } else {
        at = sdfIndirectConeBounceProc.position + sdfIndirectConeBounceProc.normal * sdfIndirectConeBounceProc.offset;
    }
    sdfIndirectConeBounceProc.resume = SdfIndirectConeSampled;
    return sdfIndirectAsk(at, SDF_INSTANCE_MASK_ALL);
}

// Whether a comparison method replaces this pixel's incoming light this frame: one render-pixel parity class a frame.
bool sdfIndirectAlternativeAdmitted(SdfPixel p) {
    if (passGroup.indirectMethod == SdfIndirectMethodCache || passGroup.indirectTier == SdfIndirectTierOff) { return false; }
    uint phase = passGroup.historyFrames % SdfIndirectAlternativePhases;
    return ((p.pixel.x & 1u) | ((p.pixel.y & 1u) << 1u)) == phase;
}

// The comparison methods' normalized set of incoming diffuse samples over the cache fallback: each ray's screen bounce
// reads the current visibility records, and each cone bounce is the one call of this procedure
// (sdfIndirectAlternativeStep), so every ray shares one bounded ray body with its field and directional-shadow work.
struct SdfIndirectAlternativeProc {
    SdfPixel p;
    float3 normal;
    float3 launched;
    SdfIndirectSources fallback;
    SdfIndirectSources total;
    uint phase;
    uint hits;
    uint samples;
    uint unresolved;
    uint ray;
    bool previousSecondary;
    bool previousShadow;
    bool resuming;
};
static SdfIndirectAlternativeProc sdfIndirectAlternativeProc = (SdfIndirectAlternativeProc)0;

uint sdfIndirectAlternativeBegin(SdfPixel p, float3 normal, float3 launched, SdfIndirectSources fallback) {
    sdfIndirectAlternativeProc.p = p;
    sdfIndirectAlternativeProc.normal = normal;
    sdfIndirectAlternativeProc.launched = launched;
    sdfIndirectAlternativeProc.fallback = fallback;
    sdfIndirectAlternativeProc.resuming = false;
    return SdfIndirectProcAlternative;
}

// Folds one ray's answer into the total: an unanswered ray keeps the fallback, and a secondary surface keeps the cache's
// sky.
void sdfIndirectAlternativeTake(bool answered, bool hitSurface, bool screenTerminal, SdfIndirectSources incoming) {
    SdfIndirectSources fallback = sdfIndirectAlternativeProc.fallback;
    if (!answered) { incoming = fallback; sdfIndirectAlternativeProc.unresolved++; }
    else if (hitSurface) {
        sdfIndirectAlternativeProc.hits++;
        // A secondary surface has no certified sky hemisphere. The cache owns this component until a
        // visibility-weighted secondary integral replaces it; raw harmonic ambient would leak through walls.
        // A screen is an emission terminal, including a valid dark answer; no cached sky passes through it.
        if (!screenTerminal) { incoming.values[SdfIndirectSourceSky] = fallback.values[SdfIndirectSourceSky]; }
    }
    [unroll] for (uint source = 0u; source < SdfIndirectSourceCount; source++) { sdfIndirectAlternativeProc.total.values[source] += incoming.values[source] / (float)SdfIndirectAlternativeRays; }
}

uint sdfIndirectAlternativeStep() {
    if (!sdfIndirectAlternativeProc.resuming) {
        if (!sdfIndirectAlternativeAdmitted(sdfIndirectAlternativeProc.p)) {
            sdfIndirectAlternativeProc.total = sdfIndirectAlternativeProc.fallback;
            return SdfIndirectStepReturn;
        }
        sdfIndirectAlternativeProc.phase = passGroup.historyFrames % SdfIndirectAlternativePhases;
        sdfIndirectAlternativeProc.total = (SdfIndirectSources)0;
        sdfIndirectAlternativeProc.hits = 0u;
        sdfIndirectAlternativeProc.samples = 0u;
        sdfIndirectAlternativeProc.unresolved = 0u;
        sdfIndirectAlternativeProc.previousSecondary = sdfSecondaryMarchActive;
        sdfIndirectAlternativeProc.previousShadow = sdfShadowParticipationActive;
        sdfSecondaryMarchActive = true;
        sdfShadowParticipationActive = true;
        sdfIndirectAlternativeProc.ray = 0u;
        sdfIndirectAlternativeProc.resuming = true;
    } else {
        sdfIndirectAlternativeProc.samples = sdfIndirectConeBounceProc.samples;
        sdfIndirectAlternativeTake(sdfIndirectConeBounceProc.answered, sdfIndirectConeBounceProc.hitSurface,
            sdfIndirectConeBounceProc.screenTerminal, sdfIndirectConeBounceProc.sources);
        sdfIndirectAlternativeProc.ray++;
    }
    [loop]
    for (; sdfIndirectAlternativeProc.ray < SdfIndirectAlternativeRays; sdfIndirectAlternativeProc.ray++) {
        float3 direction = sdfIndirectAlternativeDirection(sdfIndirectAlternativeProc.normal, sdfIndirectAlternativeProc.ray, sdfIndirectAlternativeProc.phase);
        if (passGroup.indirectMethod == SdfIndirectMethodScreen) {
            SdfIndirectSources incoming;
            bool screenTerminal;
            uint samples = sdfIndirectAlternativeProc.samples;
            bool answered = sdfIndirectScreenBounce(sdfIndirectAlternativeProc.p, sdfIndirectAlternativeProc.launched, direction, incoming, screenTerminal, samples);
            sdfIndirectAlternativeProc.samples = samples;
            sdfIndirectAlternativeTake(answered, answered, screenTerminal, incoming);
            continue;
        }
        return sdfIndirectCall(sdfIndirectConeBounceBegin(sdfIndirectAlternativeProc.launched, direction, sdfIndirectAlternativeProc.fallback,
            sdfIndirectAlternativeProc.p.farDistance, sdfIndirectAlternativeProc.samples));
    }
    sdfShadowParticipationActive = sdfIndirectAlternativeProc.previousShadow;
    sdfSecondaryMarchActive = sdfIndirectAlternativeProc.previousSecondary;
    puckCountIndirect(SDF_SKY_DETAIL_INDIRECT, sdfIndirectAlternativeProc.hits, sdfIndirectAlternativeProc.samples, sdfIndirectAlternativeProc.unresolved);
    return SdfIndirectStepReturn;
}
#endif
