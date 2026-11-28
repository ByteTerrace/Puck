// The glass shading pass rewrites the lit image after views and before sky and resolve. It copies pixels that have neither a
// front face before its opaque hit or a ray that starts inside a solid. The pass shades up to SDF_GLASS_LAYERS
// interfaces: the nearest front face (the glass target's) and the next nearest after it in the sums' layer list
// (sdfGlassNextLayer), nearest first, so the order the faces rasterized in never shows. Each interface is shaded alike (one
// loop body, never a copy):
// - its surface is the glass draw's triangle at the ray's hit, normal and material resolved as the surface pass resolves
//   an opaque hit (sdfMeshSurfaceNormal, sdfMeshMaterial), its index of refraction and absorption the material's
//   (sdfMaterialLoad) and its frost the material's roughness; a material's flow layer moves its shading as on an opaque
//   surface (sdfShadeFlow, the one flow layer), tilting the normal it reflects and refracts by;
// - the distance the ray travels through glass is the glass sums' (frame/sdf-glass.hlsli), so a hollow shell absorbs
//   over both its walls and a solid holding an opaque object over the glass before it. Of several interfaces the nearest
//   one's share runs from it to the nearest back face (or to the next interface, whichever is nearer), each later one
//   but the last takes an even part of the glass left, never past the next interface, each absorbing by its own material,
//   and the last takes the rest of the distance and of the optical depth (sdfGlassShare);
// - its own emission (the material's emissive, scaled by its emissive lane) joins what it shows, so an emissive glass
//   glows over what lies behind it as an emissive surface does;
// - a medium that scatters (SdfGlass.Scatter, Scattering) shows its own light over a share 1 - exp(-scattering *
//   distance) of the interface, the light arriving at the surface as a surface of the scatter colour would show it
//   (sdfGlassReceived), and what lies behind over the rest;
// - a contact (sdfGlassContact) adds coverage to the flow's where the opaque hit lies within its reach of the surface,
//   and the coverage the flow reveals covers the interface as its revealed surface, lit diffusely by the same light;
// - the near interface's irradiance gather comes from the glass probe history where a preceding render kept one for a
//   receiver its own passes the reuse test against (sdfGlassNearProbes, sdf-probe-history.hlsli), and is gathered and
//   kept for the next render otherwise; the far interface and a medium the ray starts in gather every time;
// - its surroundings reflect in it by Fresnel (Schlick over the index's normal reflectance): the sky the lighting sees,
//   blurred by the roughness (worldEnvironmentReflection), occluded by the irradiance volume and replaced by the light its
//   probes measured where they do not see the sky (sdfMeshReflection), as an opaque surface reflects them; where no probe
//   answers, the sky as far as the camera sample sees it open (sdfMeshReflectionSeen). Each light adds its highlight
//   through the one light interface (sdfLightResponse), its shadowing read from the probes, since the glass casts into no
//   cascade and reads none. The surface's local adaptation scales its reflection, highlights and medium alike, and they
//   pass through the atmosphere's transmittance over the interface's own distance.
// The light behind both is the lit image, read once: where the ray, bent by Snell's law at each interface and carried
// through its share of the glass, then on parallel to the pixel's ray, reaches the opaque hit's depth, projected back to
// the render grid, and read at the pixel's own sample where that lands on a nearer surface; frost spreads the read over
// a disc of taps whose radius grows with each interface's roughness and its distance to what lies behind, turned by a
// per-pixel pattern that never moves between frames, each tap left out where it lands on a surface nearer than the
// glass. Each interface's emission is under the surface's local adaptation, as an opaque surface's is. The interfaces
// compose front to back: each adds what it shows under the light every nearer interface passes, so what an interface
// transmits is the shading of every interface behind it and, last, the light behind them all.
// The glass pixel's coverage is the light behind's, darkened by the luminance of the RGB share the interfaces pass, and
// that share goes to the composite in the sky filter (frame/sdf-glass-sky-filter.hlsli), so the sky the composite puts
// behind the glass shows through it at the pixel's own direction, unrefracted and unfrosted but tinted and darkened as
// the light behind is; a revealed surface covers it. In a temporal view the pixel's reactivity is raised to one where the refracted
// read comes from a pixel away or more, since the resolve's history follows the opaque hit the record names, not what the
// glass shows; a flat pane bending nothing keeps its history and its edges resolve like any other. A debug view's lit
// image passes through. The pass counts each interface it shades as a work step.
// Where it stops: transmission integrates every glass material before the opaque hit, but only the nearest
// SDF_GLASS_LAYERS interfaces reflect, refract, emit and scatter. A farther front face's glass absorbs within the last
// interface's share and what lies beyond it is the opaque image, never shaded as glass. The later interfaces' shares
// assume panes alike, since the sums hold only the nearest exit. A ray that starts in glass takes its starting segment in
// front of these interfaces.
#define SDF_DYNAMIC_TRANSFORMS
#include "../isa/sdf-glass.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-visibility.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../frame/sdf-debug-modes.hlsli"
#include "../field/sdf-program.hlsli"
#include "../shade/sdf-material.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-lights.hlsli"
#include "../frame/sdf-glass.hlsli"
#include "../frame/sdf-glass-sky-filter.hlsli"
#include "../shade/sdf-sky-reflection.hlsli"
#include "../shade/sdf-light.hlsli"
#include "../shade/shade-flow.hlsli"
#include "../field/sdf-point.hlsli"
#include "../field/sdf-shapes.hlsli"
#include "../shade/shade-layers.hlsli"
#include "../isa/sdf-shade.hlsli"
#include "../shade/sdf-transport.hlsli"
#include "sdf-probe-history.hlsli"

// The frost's taps and the most render pixels their disc spans.
static const uint GlassFrostTaps = 8u;
static const float GlassFrostMaxPixels = 24.0;
// The scatter a fully frosted glass spreads light over, as a fraction of the distance behind it.
static const float GlassFrostSpread = 0.25;
#include "Shared/low-discrepancy.hlsli"

// Projects a world point into the render grid of `view`, whose lens row carries this sample's jitter, as
// cameraRayDirection maps a pixel to its ray: continuous coordinates with the first pixel's centre at (0.5, 0.5) and
// positive Y down. `forward` is the point's forward distance; a point not beyond the eye projects nowhere useful.
float2 sdfGlassProject(ViewportData view, float3 at, uint2 extent, out float forward) {
    float3 relative = (at - view.position.xyz);

    forward = dot(relative, view.forward.xyz);

    float inverse = (1.0 / max(forward, 1.0e-4));
    float x = ((dot(relative, view.right.xyz) * inverse) - view.lens.y);
    float y = ((dot(relative, view.up.xyz) * inverse) - view.lens.z);
    float2 ndc = float2((x / (view.right.w * view.up.w)), (y / view.right.w));

    return (float2(((ndc.x * 0.5) + 0.5), (0.5 - (ndc.y * 0.5))) * float2(extent));
}
// Whether the lit image at a continuous render-grid position shows what lies behind a glass face at ray distance `nearT`:
// false where the position lands on a surface nearer than the glass, which a read there would show in front of it.
bool sdfGlassReadsBehind(float2 at, uint2 extent, float nearT) {
    uint2 atPixel = (uint2)clamp(at, float2(0.0, 0.0), (float2(extent) - 1.0));
    SdfVisibility atVisibility = sdfLoadVisibility(sdfVisibilityRecord(atPixel, 0u, extent));

    return !(sdfVisibilityHit(atVisibility) && (atVisibility.t < nearT));
}
// The lit image at a continuous render-grid position, bilinearly, clamped to the grid, its light in nits
// (sdfStoredLightScale divided out).
float4 sdfGlassLitAt(float2 at, uint2 extent) {
    float2 clamped = clamp(at, float2(0.5, 0.5), (float2(extent) - 0.5));
    float4 stored = lit.SampleLevel(samplers[SDF_FILTER_LINEAR], (clamped / float2(extent)), 0);

    return float4((stored.rgb / sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale)), stored.a);
}
// The coverage a glass's contact adds to its flow's at a distance `behind` from the surface to the opaque hit along the
// ray: the material's contact coverage where the opaque surface touches the glass, falling smoothly to zero at its reach;
// zero for a material with no contact (row 20.w's halves, KEEP IN SYNC with SdfAppearance.Pack).
float sdfGlassContact(SdfMaterialData material, float behind) {
    if ((material.contactReach <= 0.0) || (material.contactCoverage <= 0.0)) {
        return 0.0;
    }

    return (material.contactCoverage * (1.0 - smoothstep(0.0, material.contactReach, behind)));
}
// The light arriving at the glass's surface as a white surface there would show it, the radiance the medium scatters and
// a revealed surface reflects, and in `highlights` the lights' highlights on the glass: every light that can reach the
// points the wave's active lanes shade here (sdfWaveLightMasks, so every lane that shades an interface calls this) through
// the one interface (sdfLightResponse) at an unoccluded surface the cascades do not reach, so a shadowing light's visibility is
// the probes' (`probes`), the sky's ambient where the volume's sky source is disabled, and the volume's indirect light as
// a surface receives it. `incoming` is that light's luminance before the indirect gains, what the local adaptation meters
// (sdfLocalAdaptationGain).
float3 sdfGlassReceived(SdfMaterialData material, float3 position, float3 normal, float3 rayDirection, SdfMeshProbes probes,
    SdfShadeBinding shadeBinding, SdfShadeStamp shadeStamp, out float3 highlights, out float incoming,
    out float3 radiance, out float3 indirect, out float3 rim, out float attenuation) {
    SdfShadeSurface surface;

    surface.position = position;
    surface.normal = normal;
    surface.rayDirection = rayDirection;
    surface.material = material;
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = float4(1.0, 1.0, 1.0, 1.0);
    surface.shadowTint = float3(1.0, 1.0, 1.0);
    surface.shadowMeasured = float4(1.0, 1.0, 1.0, 1.0);
    surface.cascaded = false;
    surface.probed = sdfMeshAnswers(probes);
    surface.probes = probes;

    // The view's lights and occluders that can reach a point some lane shading glass beside this one shades
    // (sdfWaveLightMasks). The occluders take their share of the ambient and indirect light the glass receives
    // (sdfOccludersAmbient) and shadow the ranged lights in the walk.
    SdfLightMasks masks = sdfWaveLightMasks(position);
    float analyticOcclusion = sdfOccludersAmbient(masks.occluders, position, normal, SDF_TRANSFORM_SLOT_NONE);

    surface.occluders = masks.occluders;
    surface.frameSlot = SDF_TRANSFORM_SLOT_NONE;
    // Glass weighs its lights by the view's cutoff alone, as its wave's masks keep them (sdfWaveLightMasks).
    surface.lightFloor = 0.0;

    bool indirectSky = ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u);
    radiance = (indirectSky ? float3(0.0, 0.0, 0.0) : (worldSkyIrradiance(normal) * analyticOcclusion));
    SdfLightWalk walk = sdfLightWalkStart(masks);
    SdfLightSource light;
    SdfShadeCarry shadeCarry = sdfShadeLightPrologue(shadeBinding, shadeStamp);

    highlights = float3(0.0, 0.0, 0.0);
    rim = 0.0;
    attenuation = 1.0;
    [loop]
    while (sdfLightNext(walk, light)) {
        SdfLightResponse response = sdfShadeLightPhase(shadeBinding, shadeStamp, shadeCarry, light, surface);

        radiance += response.diffuse;
        highlights += response.specular;
        if (sdfShadeBound(shadeBinding)) { rim += response.rim; attenuation *= response.attenuation; }
    }

    indirect = (sdfMeshIndirectSeen(probes, normal) * analyticOcclusion);

    incoming = dot((radiance + indirect), PuckRec709Luminance);

    return (radiance + (indirect * (passGroup.indirectApply.rgb * (material.receive * passGroup.indirectApply.w))));
}

// The ray parameter at which the pixel's ray from `origin` along `rayDirection` meets the plane of a resolved hit's
// triangle, or `far` where the ray runs along that plane.
float sdfGlassInterfaceAt(SdfMeshHit hit, float3 origin, float3 rayDirection, float far) {
    uint record = hit.record;
    uint first = (3u * hit.triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 face = cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a));
    float facing = dot(rayDirection, face);

    return ((abs(facing) > (1.0e-6 * length(face))) ? (dot((a - origin), face) / facing) : far);
}
// The layers the sums' layer list at `word` holds besides the near face (`nearDraw`, `nearTriangle`, the glass target's),
// counted without resolving them.
uint sdfGlassLayersBeyond(uint word, uint nearDraw, uint nearTriangle) {
    uint count = 0u;

    [loop]
    for (uint entry = 0u; (entry < SDF_GLASS_LAYERS); entry++) {
        uint entryDraw;
        uint entryTriangle;

        if (!sdfGlassLayerAt(word, entry, entryDraw, entryTriangle)) break;
        count += (((entryDraw != nearDraw) || (entryTriangle != nearTriangle)) ? 1u : 0u);
    }
    return count;
}
// The next glass front face after `previousT` from the sums' layer list at `word`, which holds the nearest faces in
// distance order (frame/sdf-glass.hlsli): the list's entry at `cursor` or after it other than the near face (`nearDraw`,
// `nearTriangle`, the glass target's), its ray parameter its plane's, held between `previousT` and the opaque hit, so
// two faces whose codes tie keep the list's order. `cursor` moves past the entry taken. An entry the mesh hit door
// rejects is no face. False where the list holds no further face.
bool sdfGlassNextLayer(uint word, inout uint cursor, uint nearDraw, uint nearTriangle, float3 origin, float3 rayDirection, float previousT, float opaque,
    out SdfMeshHit next, out float t) {
    next = (SdfMeshHit)0;
    t = opaque;
    [loop]
    for (; (cursor < SDF_GLASS_LAYERS); cursor++) {
        uint entryDraw;
        uint entryTriangle;

        if (!sdfGlassLayerAt(word, cursor, entryDraw, entryTriangle)) {
            cursor = SDF_GLASS_LAYERS;
            break;
        }
        if (((entryDraw != nearDraw) || (entryTriangle != nearTriangle)) && sdfMeshResolveHit(entryDraw, entryTriangle, next)) {
            t = clamp(sdfGlassInterfaceAt(next, origin, rayDirection, opaque), previousT, opaque);
            cursor++;
            return true;
        }
    }
    return false;
}
// The share of the glass an interface at `t` absorbs and scatters over, of the `remaining` the nearer interfaces left:
// for the nearest interface (`first`) to the nearest back face `back` or the next interface `nextT`, whichever is nearer;
// for a later one an even part of what is left among the `left` interfaces from it on, never past the next interface;
// and all that is left for the last (`last`).
float sdfGlassShare(bool first, bool last, float t, float nextT, float back, float remaining, uint left) {
    if (last) {
        return remaining;
    }
    float gap = max((nextT - t), 0.0);
    float share = (first ? ((back > t) ? min((back - t), gap) : gap) : min(gap, (remaining / float(max(left, 1u)))));

    return clamp(share, 0.0, remaining);
}

// The probes the near interface's receiver at `surfacePoint` shades with, at the normal `normal` its shading tilted it
// to, met by the ray at `cosine` in a pixel that spans `footprint` metres there: the preceding render's gather from the
// glass probe history where one stands for the receiver (sdfProbeHistoryFind), else a gather of its own. The pixel at
// its cell's first place writes the cell's entry for the next render (`writes`, set false here once it has): the gather
// it reused, carried, or the one it took, kept where it is complete. Every view's glass shades one view, so its cells are
// view zero's.
SdfMeshProbes sdfGlassNearProbes(uint2 pixel, float3 surfacePoint, float3 normal, float cosine, float footprint, bool reads, inout bool writes) {
    float2 tolerance = sdfViewsProbeTolerance(footprint, cosine);
    uint entry = sdfProbeHistoryEntry((pixel / SDF_VIEWS_PROBE_RATE), 0u);
    uint previous;

    if (reads && sdfProbeHistoryFind(sdfGlassProbeHistory, 0u, surfacePoint, normal, tolerance, previous)) {
        if (writes) {
            sdfProbeHistoryCarry(sdfGlassProbeHistory, sdfGlassProbeHistoryRW, entry, previous);
            writes = false;
        }
        return sdfProbeHistoryLoad(sdfGlassProbeHistory, previous);
    }

    bool complete;
    bool windowBound;
    SdfMeshProbes probes = sdfMeshProbesAtComplete(surfacePoint, normal, complete, windowBound);

    if (writes) {
        uint hold = sdfProbeHistoryHoldOf((surfacePoint + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS)), windowBound);

        sdfProbeHistoryStore(sdfGlassProbeHistoryRW, entry, (complete && (probes.share > 0.0)), surfacePoint, sdfVisibilityPackNormal(normal), hold, probes);
        writes = false;
    }
    return probes;
}

// The same homogeneous transport applies between the camera and an exit and behind an entrance.
float3 sdfGlassTransmission(SdfMaterialData material, float3 opticalDepth, float distance, out float3 scattered) {
    float unscattered = exp(-max(material.scattering, 0.0) * distance);
    scattered = material.scatter * (1.0 - unscattered);
    return exp(-opticalDepth) * unscattered;
}

void sdfGlassPixel(uint2 pixel) {
    uint2 extent = passGroup.imageExtent;
    // The pass works in nits and stores at the view's light scale, as the views pass does.
    float storedScale = sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale);
    float4 color = lit.Load(int3(pixel, 0));
    color.rgb /= storedScale;
    ViewportData view = worldView();
    float4 front = glassVisibility.Load(int3(pixel, 0));
    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, extent));
    float opaque = (sdfVisibilityHit(visibility) ? visibility.t : worldFarDistance(view));
    // The glass target's front face, resolved through the mesh hit door; a rejected face shades no glass.
    uint nearDraw = ((uint)front.y - 1u);
    uint nearTriangle = (uint)front.z;
    SdfMeshHit nearHit = (SdfMeshHit)0;
    bool seen = ((front.y >= 1.0) && (front.x < opaque));
    if (seen) {
        seen = sdfMeshResolveHit(nearDraw, nearTriangle, nearHit);
    }
    uint word = sdfGlassSumWord(pixel, extent);
    bool final = sdfDebugComposesFinal(passGroup.debugMode);
    bool glass = (final && seen);
    int winding = final ? sdfGlassWindingAt(word) : 0;
    if ((passGroup.debugMode == SDF_DEBUG_VIEW_SHADE_COST || passGroup.debugMode == SDF_DEBUG_VIEW_SHADE_TIER) && seen) {
        color = float4(sdfShadeDiagnostic(sdfMeshMaterial(nearHit), passGroup.debugMode), 1.0);
    }
    float reactive = 0.0;
    // The share of the pixel the composite puts the sky behind the glass in, and the RGB share of that sky the glass
    // passes (frame/sdf-glass-sky-filter.hlsli); the coverage below carries the filter's luminance.
    float skyBehind = (1.0 - color.a);
    float3 skyFilter = float3(1.0, 1.0, 1.0);
    // The glass probe history: whether this render keeps it, reads the preceding render's, and whether this pixel, its
    // cell's first, still owes the cell's entry.
    bool keepsProbes = sdfProbeHistoryKeeps(sdfGlassProbeHistoryRW, 0u);
    bool readsProbes = sdfProbeHistoryReads(sdfGlassProbeHistory, keepsProbes, extent, 0u);
    bool writesProbes = (keepsProbes && all((pixel % SDF_VIEWS_PROBE_RATE) == 0u));
    float3 origin = view.position.xyz;
    float3 rayDirection = cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(extent)));
    float insideDistance = 0.0;
    float3 insideDepth = 0.0;
    SdfMaterialData insideMaterial = (SdfMaterialData)0;
    if (winding > 0) {
        float start = worldSurfaceNearDistance(view) / dot(rayDirection, view.forward.xyz);
        float end = seen ? front.x : opaque;
        float back = sdfGlassNearestBackAt(word);
        insideDistance = max(min(back, end) - start, 0.0);
        float3 totalDepth = max(sdfGlassOpticalDepthAt(word), 0.0);
        // A single homogeneous medium has a material key. Several overlapping solids still contribute absorption.
        if (winding == 1) {
            insideMaterial = sdfMaterialLoad(sdfGlassInsideMaterialAt(word));
        }
        insideDepth = !seen ? totalDepth : min(totalDepth, insideMaterial.absorption * insideDistance);
        if (seen && !sdfMaterialIsGlass(insideMaterial)) {
            float thickness = sdfGlassThicknessAt(word, start, opaque);
            insideDepth = totalDepth * saturate(insideDistance / max(thickness, 1.0e-6));
        }
    }

    if (glass) {
        float nearT = front.x;
        float start = winding > 0 ? worldSurfaceNearDistance(view) / dot(rayDirection, view.forward.xyz) : nearT;
        float thickness = clamp(sdfGlassThicknessAt(word, start, opaque) - insideDistance, 0.0, opaque - nearT);
        float3 depth = max(sdfGlassOpticalDepthAt(word) - insideDepth, 0.0);
        float footprint = ((2.0 * view.right.w) / max(float(extent.y), 1.0));
        // The interfaces nearest first: the near face, then the layer list's faces after it, each found one ahead so an
        // interface's share can reach to the next.
        uint layers = min((1u + sdfGlassLayersBeyond(word, nearDraw, nearTriangle)), SDF_GLASS_LAYERS);
        uint cursor = 0u;
        SdfMeshHit hit = nearHit;
        float t = nearT;
        SdfMeshHit nextHit;
        float nextT;
        bool hasNext = ((layers > 1u) && sdfGlassNextLayer(word, cursor, nearDraw, nearTriangle, origin, rayDirection, t, opaque, nextHit, nextT));
        float back = sdfGlassNearestBackAt(word);
        // The glass and optical depth the nearer interfaces left.
        float remaining = thickness;
        float3 depthLeft = depth;
        // The composition front to back: the light the interfaces so far show toward the eye and the share of the light
        // behind them they pass (RGB).
        float3 shownSoFar = float3(0.0, 0.0, 0.0);
        float3 passedSoFar = float3(1.0, 1.0, 1.0);
        // The read's displacement from the opaque hit straight down the pixel's ray, and the frost's spread there.
        float3 offset = float3(0.0, 0.0, 0.0);
        float spread = 0.0;
        uint shaded = 0u;

        [loop]
        for (uint layer = 0u; (layer < SDF_GLASS_LAYERS); layer++) {
            bool isNear = (layer == 0u);
            bool last = !hasNext;
            float share = sdfGlassShare(isNear, last, t, (hasNext ? nextT : opaque), back, remaining, (layers - layer));
            float3 surfacePoint = (origin + (rayDirection * t));
            float3 face;
            float curvature;
            int frameSlot;
            float3 normal = sdfMeshSurfaceNormal(hit, surfacePoint, rayDirection, face, curvature, frameSlot);
            int materialId = sdfMeshMaterial(hit);
            SdfMaterialData material = sdfMaterialLoad(materialId);
            SdfShadeBinding shadeBinding = sdfShadeBinding(materialId);
            float ior = max(material.ior, 1.0);
            float ratio = (ior - 1.0) / (ior + 1.0);
            float f0 = ratio * ratio;
            float3 baseAlbedo = material.albedo;
            if (sdfShadeBound(shadeBinding)) { material.specular = f0; material.metal = 0.0; material.coat = 0.0; }
            float roughness = material.roughness;

            // The flow moves the surface's shading: its tilt bends the normal the glass reflects and refracts by, and its
            // coverage, raised by the contact, is the share its revealed surface covers.
            float3 layerPoint;
            float3 layerNormal;
            float3 layerRay;
            float4 layerRotation;
            float4 lanes;
            float revealed;

            sdfLayerFrame(hit, frameSlot, surfacePoint, normal, rayDirection, layerPoint, layerNormal, layerRay, layerRotation, lanes);
            sdfApplyEmissiveLane(lanes, material);
            uint paintFirst;
            uint paintCount;
            float3 painted = sdfMaterialPaints(materialId, paintFirst, paintCount);
            float paintCoverage;
            sdfApplyPaints(surfacePoint, layerPoint, footprint * t, paintFirst, paintCount,
                (sdfShadeReadsOf(shadeBinding) & SDF_SHADE_READ_PAINT) != 0u, painted, paintCoverage);
            material.albedo = painted;
            SdfShadeStamp shadeStamp = sdfShadeStamp(material, surfacePoint, layerPoint, normal, rayDirection,
                curvature, footprint * t, lanes, sdfGlassContact(material, opaque - t), share, layerRotation, frameSlot, hit);
            sdfShadeSet_surface_geometricNormal(shadeStamp, face);
            sdfShadeSet_paint_coverage(shadeStamp, paintCoverage);
            // A program's surface phase replaces the flow layer; a program that omits it (one that only lights or composes)
            // keeps the layer as its surface, as the views stage does.
            if (sdfShadeHas(shadeBinding, 0u)) {
                sdfShadeSurfacePhase(shadeBinding, shadeStamp, material, normal, revealed);
                roughness = material.roughness;
            } else {
                normal = sdfShadeFlow(surfacePoint, normal, layerPoint, layerNormal, layerRotation, frameSlot, hit, (footprint * t), lanes,
                    sdfGlassContact(material, (opaque - t)), material, revealed);
                if (sdfShadeBound(shadeBinding)) { sdfShadeSurfaceInputs(shadeStamp, material, normal, revealed); }
            }

            // The medium's share of what the interface shows over its glass, the rest what lies behind. Each interface but
            // the last absorbs its own material over its share, never more than the sums left; the last absorbs the rest
            // of the optical depth, so the interfaces together absorb exactly the sums' whole.
            float3 absorbed = (last ? depthLeft : min((material.absorption * share), depthLeft));
            float3 medium;
            float3 transmittance = sdfGlassTransmission(material, absorbed, share, medium);

            depthLeft = max((depthLeft - absorbed), 0.0);
            remaining = max((remaining - share), 0.0);
            offset += ((refract(rayDirection, normal, (1.0 / ior)) - rayDirection) * share);
            spread += (roughness * max(((opaque - t) - share), 0.0));

            float cosine = saturate(dot(normal, -rayDirection));
            float fresnel = (f0 + ((1.0 - f0) * pow((1.0 - cosine), 5.0)));
            float air = sdfAirTransmittance(origin, rayDirection, t);

            // The GGX lobe reads the glass's own reflectance and nothing of a metal or a coat; the flow's revealed surface
            // covers the glass rather than roughening it.
            if (!sdfShadeBound(shadeBinding)) {
                material.specular = f0;
                material.metal = 0.0;
                material.coat = 0.0;
                material.roughness = roughness;
            }

            SdfMeshProbes probes;

            if (isNear) {
                probes = sdfGlassNearProbes(pixel, surfacePoint, normal, abs(dot(rayDirection, normal)), (footprint * t), readsProbes, writesProbes);
            } else {
                probes = sdfMeshProbesAt(surfacePoint, normal);
            }
            float3 highlights;
            float incoming;
            float3 radiance;
            float3 indirect;
            float3 rim;
            float attenuation;
            sdfShadeSurfaceInputs(shadeStamp, material, normal, revealed);
            float3 received = sdfGlassReceived(material, surfacePoint, normal, rayDirection, probes, shadeBinding, shadeStamp, highlights, incoming,
                radiance, indirect, rim, attenuation);
            float3 reflectDirection = reflect(rayDirection, normal);
            float3 environment = sdfMeshReflectionSeen(probes, reflectDirection, worldEnvironmentReflection(reflectDirection, roughness));

            // The surface's local adaptation (sdfLocalAdaptationGain) scales all it reflects and scatters, as it scales an
            // opaque surface's whole shade; the light through it was adapted at the opaque surface behind.
            float localGain = sdfLocalAdaptationGain(incoming);

            received *= localGain;

            // Its own emission joins what it shows, untouched by the occluders' attenuation and under the surface's local
            // adaptation, as an opaque surface's is.
            float3 emitted = ((material.albedo * material.emissive) * localGain);
            float3 shown = ((((((environment * fresnel) + highlights + rim) * localGain) + (((1.0 - fresnel) * medium) * received)) * attenuation) + emitted) * air;
            float3 passed = ((1.0 - fresnel) * transmittance);
            float3 revealedAlbedo = max((material.albedo - baseAlbedo * (1.0 - revealed)) / max(revealed, 1.0e-6), 0.0);
            float3 cover = (((sdfShadeBound(shadeBinding) ? revealedAlbedo : material.flowColor.rgb) * received) * air * attenuation);
            // An interface raises no line: the glass pass searches no edge and draws no ink.
            float linework = 0.0;

            shown = sdfShadeComposePhase(shadeBinding, shadeStamp, radiance, rim, highlights, attenuation,
                indirect, environment, environment * fresnel, localGain, shown / max(air, 1.0e-12), linework) * air;

            // Under the light the nearer interfaces pass: its revealed surface covers it over its revealed share, and the
            // rest shows its surface and passes the light behind.
            shownSoFar += (passedSoFar * lerp(shown, cover, revealed));
            passedSoFar *= ((1.0 - revealed) * passed);
            shaded++;
            if (!hasNext) {
                break;
            }
            hit = nextHit;
            t = nextT;
            hasNext = sdfGlassNextLayer(word, cursor, nearDraw, nearTriangle, origin, rayDirection, t, opaque, nextHit, nextT);
        }
        sdfWorkSteps += shaded;

        // The bent ray reaches the opaque hit's depth displaced by each interface's refraction over its share of the
        // glass: where that lands on the render grid is where the light through the glass is read.
        float3 samplePoint = ((origin + (rayDirection * opaque)) + offset);
        float forward;
        float2 at = sdfGlassProject(view, samplePoint, extent, forward);
        float2 own = (float2(pixel) + 0.5);

        // A read that lands on a surface nearer than the glass would show what stands in front of it.
        if ((forward <= 0.0) || !sdfGlassReadsBehind(at, extent, nearT)) {
            at = own;
        }

        // The frost: a disc of taps whose radius is the interfaces' roughness's share of the spread over the distance
        // behind each, in render pixels at the opaque hit's depth. A tap that lands on a surface nearer than the glass
        // (its frame, the came between its panes, an arch before it) is left out as the centre read is, so the frost
        // spreads only what lies behind the glass; where every tap lands on one, the centre read stands alone.
        float radius = min(((GlassFrostSpread * spread) / max((footprint * opaque), 1.0e-4)), GlassFrostMaxPixels);
        float4 through = float4(0.0, 0.0, 0.0, 0.0);
        uint taken = 0u;

        if (radius >= 0.5) {
            // A rotation fixed per pixel (interleaved gradient noise), so a frosted pane never changes between frames.
            float rotation = (frac(52.9829189 * frac(dot(float2(pixel), float2(0.06711056, 0.00583715)))) * 6.2831853);

            [unroll]
            for (uint tap = 0u; (tap < GlassFrostTaps); tap++) {
                float reach = (sqrt((float(tap) + 0.5) / float(GlassFrostTaps)) * radius);
                float angle = ((float(tap) * PuckGoldenAngle) + rotation);
                float2 tapAt = (at + (float2(cos(angle), sin(angle)) * reach));

                if (sdfGlassReadsBehind(tapAt, extent, nearT)) {
                    through += sdfGlassLitAt(tapAt, extent);
                    taken++;
                }
            }
        }
        through = ((taken > 0u) ? (through / float(taken)) : sdfGlassLitAt(at, extent));

        // The interfaces over the light behind them all. The sky the composite puts behind the glass shows through the
        // share of the pixel the light behind leaves uncovered, at the RGB share the interfaces pass (skyFilter).
        skyBehind = (1.0 - through.a);
        skyFilter = passedSoFar;
        color = float4((shownSoFar + (passedSoFar * through.rgb)), (1.0 - (skyBehind * dot(skyFilter, PuckRec709Luminance))));
        reactive = saturate(length(at - own));
    }

    if (insideDistance > 0.0) {
        float3 scattered;
        float3 transmitted = sdfGlassTransmission(insideMaterial, insideDepth, insideDistance, scattered);
        float3 added = 0.0;
        if (any(scattered > 0.0)) {
            float3 normal = -rayDirection;
            float start = worldSurfaceNearDistance(view) / dot(rayDirection, view.forward.xyz);
            float3 scatterPoint = origin + rayDirection * (start + 0.5 * insideDistance);
            SdfMeshProbes probes = sdfMeshProbesAt(scatterPoint, normal);
            float3 highlights, radiance, indirect, rim;
            float incoming, attenuation;
            float3 received = sdfGlassReceived(insideMaterial, scatterPoint, normal, rayDirection, probes,
                (SdfShadeBinding)0, (SdfShadeStamp)0, highlights, incoming, radiance, indirect, rim, attenuation);
            added = scattered * received * sdfLocalAdaptationGain(incoming);
        }
        color.rgb = added + transmitted * color.rgb;
        skyFilter *= transmitted;
        color.a = (1.0 - (skyBehind * dot(skyFilter, PuckRec709Luminance)));
        reactive = 1.0;
        sdfWorkSteps++;
    }
    if (glass || (insideDistance > 0.0)) {
        sdfGlassSkyFilterRW[(pixel.y * extent.x) + pixel.x] = sdfGlassSkyFilterPack(skyFilter);
    }

    // A cell whose first pixel shows no glass face keeps no gather.
    if (writesProbes) {
        sdfProbeHistoryStore(sdfGlassProbeHistoryRW, sdfProbeHistoryEntry((pixel / SDF_VIEWS_PROBE_RATE), 0u), false, float3(0.0, 0.0, 0.0), 0u, 0u, sdfMeshProbesNone());
    }
    output[pixel] = float4(min((color.rgb * storedScale), SDF_LUMINANCE_CEILING), color.a);
    if ((passGroup.temporal != 0u) && (glass || insideDistance > 0.0)) {
        uint index = sdfReactivityIndex(pixel, 0u, extent);

        reactivityRW[index] = max(reactivityRW[index], reactive);
    }
    sdfWorkTexels = 1u;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint across = (passGroup.extent.x + 7u) / 8u;
    uint group = pushedIndex.index + id.x / 8u;
    id.xy = uint2(group % across, group / across) * 8u + uint2(id.x % 8u, id.y);
    if (all(id.xy < passGroup.extent)) {
        sdfGlassPixel(id.xy);
    }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountLights(sdfWorkLights, sdfWorkFaceLights);
}
