// The glass shading pass, one invocation a pixel of the render grid, after views and before the sky, the resolve and the
// composite: writes the lit image views wrote over again, every pixel copied as it stands but one the glass target holds a
// front face at before its opaque hit. Such a pixel is shaded as glass (SdfWorldPackage.WithGlass), through up to two
// interfaces: the nearest front face (the glass target's) and the second-nearest, which the sums' second record names
// (sdfGlassSecondInterface). Each interface is shaded alike (one loop body, never a copy):
// - its surface is the glass draw's triangle at the ray's hit, normal and material resolved as the surface pass resolves
//   an opaque hit (sdfMeshSurfaceNormal, sdfMeshMaterial), its index of refraction and absorption the material's
//   (sdfMaterialLoad) and its frost the material's roughness; a material's flow layer moves its shading as on an opaque
//   surface (sdfShadeFlow, the one flow layer), tilting the normal it reflects and refracts by;
// - the distance the ray travels through glass is the glass sums' (frame/sdf-glass.hlsli), so a hollow shell absorbs
//   over both its walls and a solid holding an opaque object over the glass before it. With two interfaces the near
//   one's share runs from it to the nearest back face (or to the far interface, whichever is nearer), absorbing by its
//   own material, and the far one takes the rest of the distance and of the optical depth;
// - a medium that scatters (SdfGlass.Scatter, Scattering) shows its own light over a share 1 - exp(-scattering *
//   distance) of the interface, the light arriving at the surface as a surface of the scatter colour would show it
//   (sdfGlassReceived), and what lies behind over the rest;
// - a contact (sdfGlassContact) adds coverage to the flow's where the opaque hit lies within its reach of the surface,
//   and the coverage the flow reveals covers the interface as its revealed surface, lit diffusely by the same light;
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
// per-pixel pattern that never moves between frames. The far interface composes over that light, and the near one over
// the far one's result, so what the near interface transmits is the far interface's shading.
// The glass pixel's coverage is the light behind's, darkened by each interface's luminous transmittance, so the sky the
// composite puts behind it shows through a clear pane at the pixel's own direction, unrefracted and untinted but
// darkened; a revealed surface covers it. In a temporal view the pixel's reactivity is raised to one where the refracted
// read comes from a pixel away or more, since the resolve's history follows the opaque hit the record names, not what the
// glass shows; a flat pane bending nothing keeps its history and its edges resolve like any other. A debug view's lit
// image passes through. The pass counts each interface it shades as a work step.
// Where it stops: transmission integrates every glass material before the opaque hit, but only two interfaces reflect,
// refract and scatter. A third front face's glass absorbs within the far interface's share and what lies beyond it is
// the opaque image, never shaded as glass; past SdfGlassFrontEntries front faces the record names no second interface
// and the near one alone is shaded. The eye inside a glass solid sees no front face, so the pixel passes through.
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
#include "../shade/sdf-sky-reflection.hlsli"
#include "../shade/sdf-light.hlsli"
#include "../shade/shade-flow.hlsli"
#include "../field/sdf-point.hlsli"
#include "../field/sdf-shapes.hlsli"
#include "../shade/shade-layers.hlsli"
#include "../isa/sdf-shade.hlsli"
#include "../shade/sdf-transport.hlsli"

// The frost's taps and the most render pixels their disc spans.
static const uint GlassFrostTaps = 8u;
static const float GlassFrostMaxPixels = 24.0;
// The scatter a fully frosted glass spreads light over, as a fraction of the distance behind it.
static const float GlassFrostSpread = 0.25;
static const float GlassGoldenAngle = 2.39996323;

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
// The lit image at a continuous render-grid position, bilinearly, clamped to the grid.
float4 sdfGlassLitAt(float2 at, uint2 extent) {
    float2 clamped = clamp(at, float2(0.5, 0.5), (float2(extent) - 0.5));

    return lit.SampleLevel(samplers[SDF_FILTER_LINEAR], (clamped / float2(extent)), 0);
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
// a revealed surface reflects, and in `highlights` the lights' highlights on the glass: every light through the one
// interface (sdfLightResponse) at an unoccluded surface the cascades do not reach, so a shadowing light's visibility is
// the probes' (`probes`, `probed` while the irradiance volume is on), the sky's ambient where the volume's sky does not
// replace it, and the volume's indirect light as a surface receives it. `incoming` is that light's luminance before the
// indirect gains, what the local adaptation meters (sdfLocalAdaptationGain).
float3 sdfGlassReceived(SdfMaterialData material, float3 position, float3 normal, float3 rayDirection, bool probed, SdfMeshProbes probes,
    SdfShadeBinding shadeBinding, SdfShadeStamp shadeStamp, out float3 highlights, out float incoming,
    out float3 radiance, out float3 indirect, out float3 rim, out float attenuation) {
    SdfShadeSurface surface;

    surface.position = position;
    surface.normal = normal;
    surface.rayDirection = rayDirection;
    surface.material = material;
    surface.toon = (SdfToonStyle)0;
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = float4(1.0, 1.0, 1.0, 1.0);
    surface.shadowMeasured = float4(1.0, 1.0, 1.0, 1.0);
    surface.cascaded = false;
    surface.probed = (probed && sdfMeshAnswers(probes));
    surface.probes = probes;

    bool indirectSky = (probed && ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u));
    radiance = (indirectSky ? float3(0.0, 0.0, 0.0) : worldSkyIrradiance(normal));
    SdfLightWalk walk = sdfLightWalkStart();
    SdfLightSource light;

    highlights = float3(0.0, 0.0, 0.0);
    rim = 0.0;
    attenuation = 1.0;
    [loop]
    while (sdfLightNext(walk, light)) {
        SdfLightResponse response = sdfShadeLightPhase(shadeBinding, shadeStamp, light, surface);

        radiance += response.diffuse;
        highlights += response.specular;
        if (sdfShadeBound(shadeBinding)) { rim += response.rim; attenuation *= response.attenuation; }
    }

    indirect = (probed ? sdfMeshIndirectSeen(probes, normal) : float3(0.0, 0.0, 0.0));

    incoming = dot((radiance + indirect), SDF_LUMINANCE);

    return (radiance + (indirect * (passGroup.indirectApply.rgb * (material.receive * passGroup.indirectApply.w))));
}

// The ray parameter at which the pixel's ray from `origin` along `rayDirection` meets the plane of a draw's triangle, or
// `far` where the ray runs along that plane.
float sdfGlassInterfaceAt(uint draw, uint triangleIndex, float3 origin, float3 rayDirection, float far) {
    uint record = sdfMeshRecord(draw);
    uint first = (3u * triangleIndex);
    float3 a = sdfMeshWorldPosition(record, first);
    float3 face = cross((sdfMeshWorldPosition(record, (first + 1u)) - a), (sdfMeshWorldPosition(record, (first + 2u)) - a));
    float facing = dot(rayDirection, face);

    return ((abs(facing) > (1.0e-6 * length(face))) ? (dot((a - origin), face) / facing) : far);
}
// The second-nearest glass front face before the opaque hit, from the sums' second record at `word`: the held front
// face nearest along the ray other than the near one (`nearDraw`, `nearTriangle`, the glass target's), ties going to the
// lower draw and triangle, so the answer never depends on the order the faces claimed their entries. False where fewer
// than two front faces lie before the opaque hit, or more than the record holds.
bool sdfGlassSecondInterface(uint word, uint nearDraw, uint nearTriangle, float3 origin, float3 rayDirection, float nearT, float opaque,
    out uint draw, out uint triangleIndex, out float t) {
    uint count = sdfGlassFrontCountAt(word);
    bool found = false;

    draw = 0u;
    triangleIndex = 0u;
    t = opaque;
    if ((count < 2u) || (count > SdfGlassFrontEntries)) {
        return false;
    }
    [loop]
    for (uint entry = 0u; (entry < count); entry++) {
        uint entryDraw;
        uint entryTriangle;

        sdfGlassFrontAt(word, entry, entryDraw, entryTriangle);
        if ((entryDraw != nearDraw) || (entryTriangle != nearTriangle)) {
            float at = sdfGlassInterfaceAt(entryDraw, entryTriangle, origin, rayDirection, opaque);
            bool nearer = (!found || (at < t) || ((at == t) && ((entryDraw < draw) || ((entryDraw == draw) && (entryTriangle < triangleIndex)))));

            if (nearer) {
                found = true;
                draw = entryDraw;
                triangleIndex = entryTriangle;
                t = at;
            }
        }
    }
    t = clamp(t, nearT, opaque);

    return found;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint across = (passGroup.extent.x + 7u) / 8u;
    uint group = pushedIndex.index + id.x / 8u;
    id.xy = uint2(group % across, group / across) * 8u + uint2(id.x % 8u, id.y);
    if (any(id.xy >= passGroup.extent)) {
        return;
    }

    uint2 pixel = id.xy;
    uint2 extent = passGroup.imageExtent;
    float4 color = lit.Load(int3(pixel, 0));
    ViewportData view = worldView();
    float4 front = glassVisibility.Load(int3(pixel, 0));
    SdfVisibility visibility = sdfLoadVisibility(sdfVisibilityRecord(pixel, 0u, extent));
    float opaque = (sdfVisibilityHit(visibility) ? visibility.t : worldFarDistance(view));
    bool glass = (sdfDebugComposesFinal(passGroup.debugMode) && (front.y >= 1.0) && (front.x < opaque));
    if ((passGroup.debugMode == SDF_DEBUG_VIEW_SHADE_COST || passGroup.debugMode == SDF_DEBUG_VIEW_SHADE_TIER) && front.y >= 1.0 && front.x < opaque) {
        color = float4(sdfShadeDiagnostic(sdfMeshMaterial((uint)front.y - 1u, (uint)front.z), passGroup.debugMode), 1.0);
    }
    float reactive = 0.0;

    if (glass) {
        uint nearDraw = ((uint)front.y - 1u);
        uint nearTriangle = (uint)front.z;
        float nearT = front.x;
        float3 origin = view.position.xyz;
        float3 rayDirection = cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(extent)));
        uint word = sdfGlassSumWord(pixel, extent);
        float thickness = sdfGlassThicknessAt(word, nearT, opaque);
        float3 depth = max(sdfGlassOpticalDepthAt(word), 0.0);
        float footprint = ((2.0 * view.right.w) / max(float(extent.y), 1.0));
        uint farDraw;
        uint farTriangle;
        float farT;
        bool second = sdfGlassSecondInterface(word, nearDraw, nearTriangle, origin, rayDirection, nearT, opaque, farDraw, farTriangle, farT);
        uint layers = (second ? 2u : 1u);
        // The near interface's share of the glass: to the nearest back face, never past the far interface; the far one
        // takes the rest.
        float back = sdfGlassNearestBackAt(word);
        float nearThickness = (second ? clamp(((back > nearT) ? (min(back, farT) - nearT) : (farT - nearT)), 0.0, thickness) : thickness);
        float farThickness = (thickness - nearThickness);
        float3 nearDepth = float3(0.0, 0.0, 0.0);
        // Each interface's light toward the eye, kept by comparison rather than in an indexed local array: what its
        // surface shows (`*Shown`, the reflection, highlights and medium), what it passes of the light behind
        // (`*Passed`, RGB), what its revealed surface shows (`*Cover`) over its revealed share, and its luminous share
        // passed.
        float3 nearShown = float3(0.0, 0.0, 0.0);
        float3 nearPassed = float3(0.0, 0.0, 0.0);
        float3 nearCover = float3(0.0, 0.0, 0.0);
        float nearRevealed = 0.0;
        float nearLuminous = 0.0;
        float3 farShown = float3(0.0, 0.0, 0.0);
        float3 farPassed = float3(0.0, 0.0, 0.0);
        float3 farCover = float3(0.0, 0.0, 0.0);
        float farRevealed = 0.0;
        float farLuminous = 0.0;
        // The read's displacement from the opaque hit straight down the pixel's ray, and the frost's spread there.
        float3 offset = float3(0.0, 0.0, 0.0);
        float spread = 0.0;

        [loop]
        for (uint layer = 0u; (layer < layers); layer++) {
            bool isNear = (layer == 0u);
            uint draw = (isNear ? nearDraw : farDraw);
            uint triangleIndex = (isNear ? nearTriangle : farTriangle);
            float t = (isNear ? nearT : farT);
            float share = (isNear ? nearThickness : farThickness);
            float3 surfacePoint = (origin + (rayDirection * t));
            float3 face;
            float curvature;
            int frameSlot;
            float3 normal = sdfMeshSurfaceNormal(draw, triangleIndex, surfacePoint, rayDirection, face, curvature, frameSlot);
            int materialId = sdfMeshMaterial(draw, triangleIndex);
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
            float revealed;

            sdfLayerFrame(frameSlot, surfacePoint, normal, rayDirection, layerPoint, layerNormal, layerRay, layerRotation);
            uint paintFirst;
            uint paintCount;
            float3 painted = sdfMaterialPaints(materialId, paintFirst, paintCount);
            float paintCoverage;
            sdfApplyPaints(surfacePoint, layerPoint, footprint * t, paintFirst, paintCount,
                (sdfShadeReadsOf(shadeBinding) & SDF_SHADE_READ_PAINT) != 0u, painted, paintCoverage);
            material.albedo = painted;
            SdfShadeStamp shadeStamp = sdfShadeStamp(material, surfacePoint, layerPoint, normal, rayDirection,
                curvature, footprint * t, sdfFrameLanes(frameSlot), sdfGlassContact(material, opaque - t), share, layerRotation, frameSlot, draw);
            sdfShadeSet_surface_geometricNormal(shadeStamp, face);
            sdfShadeSet_paint_coverage(shadeStamp, paintCoverage);
            if (sdfShadeBound(shadeBinding)) {
                sdfShadeSurfacePhase(shadeBinding, shadeStamp, material, normal, revealed);
                roughness = material.roughness;
            } else {
                normal = sdfShadeFlow(surfacePoint, normal, layerPoint, layerNormal, layerRotation, frameSlot, draw, (footprint * t), sdfFrameLanes(frameSlot),
                    sdfGlassContact(material, (opaque - t)), material, revealed);
            }

            // The medium's share of what the interface shows over its glass, the rest what lies behind. A lone
            // interface absorbs the whole optical depth; of two, the near absorbs its own material over its share and
            // the far the rest.
            float3 absorbed = (second ? (isNear ? (material.absorption * share) : max((depth - nearDepth), 0.0)) : depth);
            float haze = (1.0 - exp(-(max(material.scattering, 0.0) * share)));
            float3 transmittance = (exp(-absorbed) * (1.0 - haze));
            float3 medium = (material.scatter * haze);

            nearDepth = (isNear ? absorbed : nearDepth);
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

            bool probed = sdfMeshIrradianceEnabled();
            SdfMeshProbes probes = sdfMeshProbesAt(surfacePoint, normal);
            float3 highlights;
            float incoming;
            float3 radiance;
            float3 indirect;
            float3 rim;
            float attenuation;
            sdfShadeSurfaceInputs(shadeStamp, material, normal, revealed);
            float3 received = sdfGlassReceived(material, surfacePoint, normal, rayDirection, probed, probes, shadeBinding, shadeStamp, highlights, incoming,
                radiance, indirect, rim, attenuation);
            float3 reflectDirection = reflect(rayDirection, normal);
            float3 environment = worldEnvironmentReflection(reflectDirection, roughness);

            if (probed) {
                environment = sdfMeshReflectionSeen(probes, reflectDirection, environment);
            }

            // The surface's local adaptation (sdfLocalAdaptationGain) scales all it reflects and scatters, as it scales an
            // opaque surface's whole shade; the light through it was adapted at the opaque surface behind.
            float localGain = sdfLocalAdaptationGain(incoming);

            received *= localGain;

            float3 shown = (((((environment * fresnel) + highlights + rim) * localGain) + (((1.0 - fresnel) * medium) * received)) * air * attenuation);
            float3 passed = ((1.0 - fresnel) * transmittance);
            float3 revealedAlbedo = max((material.albedo - baseAlbedo * (1.0 - revealed)) / max(revealed, 1.0e-6), 0.0);
            float3 cover = (((sdfShadeBound(shadeBinding) ? revealedAlbedo : material.flowColor.rgb) * received) * air * attenuation);
            shown = sdfShadeComposePhase(shadeBinding, shadeStamp, radiance, rim, highlights, attenuation,
                indirect, environment, environment * fresnel, localGain, shown / max(air, 1.0e-12)) * air;
            float luminous = ((1.0 - fresnel) * dot(transmittance, SDF_LUMINANCE));

            if (isNear) {
                nearShown = shown;
                nearPassed = passed;
                nearCover = cover;
                nearRevealed = revealed;
                nearLuminous = luminous;
            } else {
                farShown = shown;
                farPassed = passed;
                farCover = cover;
                farRevealed = revealed;
                farLuminous = luminous;
            }
        }
        sdfWorkSteps += layers;

        // The bent ray reaches the opaque hit's depth displaced by each interface's refraction over its share of the
        // glass: where that lands on the render grid is where the light through the glass is read.
        float3 samplePoint = ((origin + (rayDirection * opaque)) + offset);
        float forward;
        float2 at = sdfGlassProject(view, samplePoint, extent, forward);
        float2 own = (float2(pixel) + 0.5);

        if (forward <= 0.0) {
            at = own;
        } else {
            // A read that lands on a surface nearer than the glass would show what stands in front of it.
            uint2 atPixel = (uint2)clamp(at, float2(0.0, 0.0), (float2(extent) - 1.0));
            SdfVisibility atVisibility = sdfLoadVisibility(sdfVisibilityRecord(atPixel, 0u, extent));

            if (sdfVisibilityHit(atVisibility) && (atVisibility.t < nearT)) {
                at = own;
            }
        }

        // The frost: a disc of taps whose radius is the interfaces' roughness's share of the spread over the distance
        // behind each, in render pixels at the opaque hit's depth.
        float radius = min(((GlassFrostSpread * spread) / max((footprint * opaque), 1.0e-4)), GlassFrostMaxPixels);
        float4 through;

        if (radius < 0.5) {
            through = sdfGlassLitAt(at, extent);
        } else {
            // A rotation fixed per pixel (interleaved gradient noise), so a frosted pane never changes between frames.
            float rotation = (frac(52.9829189 * frac(dot(float2(pixel), float2(0.06711056, 0.00583715)))) * 6.2831853);

            through = float4(0.0, 0.0, 0.0, 0.0);
            [unroll]
            for (uint tap = 0u; (tap < GlassFrostTaps); tap++) {
                float reach = (sqrt((float(tap) + 0.5) / float(GlassFrostTaps)) * radius);
                float angle = ((float(tap) * GlassGoldenAngle) + rotation);

                through += sdfGlassLitAt((at + (float2(cos(angle), sin(angle)) * reach)), extent);
            }
            through /= float(GlassFrostTaps);
        }

        // The far interface composes over the light behind, and the near one over that.
        float3 light = through.rgb;
        float coverage = through.a;

        if (second) {
            light = lerp((farShown + (farPassed * light)), farCover, farRevealed);
            coverage = lerp((1.0 - ((1.0 - coverage) * farLuminous)), 1.0, farRevealed);
        }
        color = float4(lerp((nearShown + (nearPassed * light)), nearCover, nearRevealed), lerp((1.0 - ((1.0 - coverage) * nearLuminous)), 1.0, nearRevealed));
        reactive = saturate(length(at - own));
    }

    output[pixel] = float4(min(color.rgb, SDF_LUMINANCE_CEILING), color.a);
    if ((passGroup.temporal != 0u) && glass) {
        uint index = sdfReactivityIndex(pixel, 0u, extent);

        reactivityRW[index] = max(reactivityRW[index], reactive);
    }
    sdfWorkTexels = 1u;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
