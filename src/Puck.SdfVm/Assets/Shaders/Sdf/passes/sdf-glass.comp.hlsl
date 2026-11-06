// The glass shading pass, one invocation a pixel of the render grid, after views and before the sky, the resolve and the
// composite: writes the lit image views wrote over again, every pixel copied as it stands but one the glass target holds a
// front face at before its opaque hit. Such a pixel is shaded as glass (SdfWorldPackage.WithGlass):
// - its surface is the glass draw's triangle at the ray's hit, normal and material resolved as the surface pass resolves
//   an opaque hit (sdfMeshSurfaceNormal, sdfMeshMaterial), its index of refraction and absorption the material's
//   (sdfMaterialLoad) and its frost the material's roughness; a material's flow layer moves its shading as on an opaque
//   surface (sdfShadeFlow, the one flow layer), tilting the normal it reflects and refracts by;
// - the distance the ray travels through glass is the glass sums' (frame/sdf-glass.hlsli), so a hollow shell absorbs
//   over both its walls and a solid holding an opaque object over the glass before it, and the light through it is the
//   lit image behind, absorbed over that distance by Beer–Lambert (exp(-absorption * distance));
// - that light is read where the ray bent by Snell's law leaves the glass, carried on parallel to the pixel's ray to the
//   opaque hit's depth and projected back to the render grid, so what lies behind the glass shifts as it does through a
//   slab, and read at the pixel's own sample where that lands on a nearer surface; frost spreads the read over a disc of
//   taps whose radius grows with the roughness and the distance from the glass to what lies behind, turned by a
//   per-pixel pattern that never moves between frames;
// - a medium that scatters (SdfGlass.Scatter, Scattering) shows its own light over a share 1 - exp(-scattering *
//   distance) of the glass, the light arriving at the surface as a surface of the scatter colour would show it
//   (sdfGlassReceived), and the light behind over the rest, so a deep body takes the scatter colour and a shallow margin
//   shows what lies behind;
// - a contact (sdfGlassContact) adds coverage to the flow's where the opaque hit lies within its reach of the surface,
//   and the coverage the flow reveals covers the glass as its revealed surface, lit diffusely by the same arriving light;
// - its surroundings reflect in it by Fresnel (Schlick over the index's normal reflectance): the sky the lighting sees,
//   blurred by the roughness (worldEnvironmentReflection), occluded by the irradiance volume and replaced by the light its
//   probes measured where they do not see the sky (sdfMeshReflection), as an opaque surface reflects them; where no probe
//   answers, the sky alone. Each light adds its highlight through the one light interface (sdfLightResponse), its
//   shadowing read from the probes, since the glass casts into no cascade and reads none. Reflection, medium and
//   revealed surface pass through the atmosphere's transmittance over the glass's own distance; the light through the
//   glass carries the atmosphere over the opaque hit's distance already.
// The glass pixel's coverage is the light behind's, darkened by the glass's luminous transmittance, so the sky the
// composite puts behind it shows through a clear pane at the pixel's own direction, unrefracted and untinted but
// darkened; a revealed surface covers it. In a temporal view the pixel's reactivity is raised to one where the refracted
// read comes from a pixel away or more, since the resolve's history follows the opaque hit the record names, not what the
// glass shows; a flat pane bending nothing keeps its history and its edges resolve like any other. A debug view's lit
// image passes through.
// Transmission integrates each glass material before the opaque hit, including glass behind glass; scattering, contact
// and flow are the nearest front face's material's. Reflection and refraction use the nearest front face; farther
// interfaces do not contribute separate reflected images. The pixel reads the lit image once (or the frost's disc): the
// medium, the contact and the revealed surface are functions of the thickness, the distance to the opaque hit and the
// arriving light alone.
// The eye inside a glass solid sees no front face, so the pixel passes through.
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
// zero for a material with no contact (row 20.w's halves, KEEP IN SYNC with SdfProgram.PackMaterials).
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
    out float3 highlights, out float incoming) {
    SdfShadeSurface surface;

    surface.position = position;
    surface.normal = normal;
    surface.rayDirection = rayDirection;
    surface.material = material;
    surface.toon = (SdfToonStyle)0;
    surface.ambientOcclusion = 1.0;
    surface.shadowVisibility = float4(1.0, 1.0, 1.0, 1.0);
    surface.cascaded = false;
    surface.probed = probed;
    surface.probes = probes;

    bool indirectSky = (probed && ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u));
    float3 radiance = (indirectSky ? float3(0.0, 0.0, 0.0) : worldSkyIrradiance(normal));
    uint count = sdfLightCount();

    highlights = float3(0.0, 0.0, 0.0);
    [loop]
    for (uint index = 0u; (index < count); index++) {
        SdfLightSource light;

        if (!sdfLightAt(index, light)) {
            continue;
        }

        SdfLightResponse response = sdfLightResponse(light, surface);

        radiance += response.diffuse;
        highlights += response.specular;
    }

    float3 indirect = (probed ? sdfMeshIndirect(probes, normal) : float3(0.0, 0.0, 0.0));

    incoming = dot((radiance + indirect), SDF_LUMINANCE);

    return (radiance + (indirect * (passGroup.indirectApply.rgb * (material.receive * passGroup.indirectApply.w))));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
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
    float reactive = 0.0;

    if (glass) {
        uint draw = ((uint)front.y - 1u);
        uint triangleIndex = (uint)front.z;
        float t = front.x;
        float3 origin = view.position.xyz;
        float3 rayDirection = cameraRayDirection(view, ((float2(pixel) + 0.5) / float2(extent)));
        float3 surfacePoint = (origin + (rayDirection * t));
        float3 face;
        float curvature;
        int frameSlot;
        float3 normal = sdfMeshSurfaceNormal(draw, triangleIndex, surfacePoint, rayDirection, face, curvature, frameSlot);
        SdfMaterialData material = sdfMaterialLoad(sdfMeshMaterial(draw, triangleIndex));
        float ior = max(material.ior, 1.0);
        float roughness = material.roughness;
        uint word = sdfGlassSumWord(pixel, extent);
        float thickness = sdfGlassThicknessAt(word, t, opaque);
        float behind = max(((opaque - t) - thickness), 0.0);
        float footprint = ((2.0 * view.right.w) / max(float(extent.y), 1.0));

        // The flow moves the surface's shading: its tilt bends the normal the glass reflects and refracts by, and its
        // coverage, raised by the contact, is the share its revealed surface covers.
        float3 layerPoint;
        float3 layerNormal;
        float3 layerRay;
        float4 layerRotation;
        float revealed;

        sdfLayerFrame(frameSlot, surfacePoint, normal, rayDirection, layerPoint, layerNormal, layerRay, layerRotation);
        normal = sdfShadeFlow(surfacePoint, normal, layerPoint, layerNormal, layerRotation, frameSlot, draw, (footprint * t), sdfFrameLanes(frameSlot),
            sdfGlassContact(material, (opaque - t)), material, revealed);

        // The medium's share of what the glass shows over its thickness, the rest the light behind.
        float haze = (1.0 - exp(-(max(material.scattering, 0.0) * thickness)));
        float3 transmittance = (exp(-max(sdfGlassOpticalDepthAt(word), 0.0)) * (1.0 - haze));
        float3 medium = (material.scatter * haze);

        // The ray bent into the glass leaves it `thickness` along, then carries on parallel to the pixel's ray to the
        // opaque hit's depth: where that lands on the render grid is where the light through the glass is read.
        float3 refracted = refract(rayDirection, normal, (1.0 / ior));
        float3 samplePoint = ((surfacePoint + (refracted * thickness)) + (rayDirection * behind));
        float forward;
        float2 at = sdfGlassProject(view, samplePoint, extent, forward);
        float2 own = (float2(pixel) + 0.5);

        if (forward <= 0.0) {
            at = own;
        } else {
            // A read that lands on a surface nearer than the glass would show what stands in front of it.
            uint2 atPixel = (uint2)clamp(at, float2(0.0, 0.0), (float2(extent) - 1.0));
            SdfVisibility atVisibility = sdfLoadVisibility(sdfVisibilityRecord(atPixel, 0u, extent));

            if (sdfVisibilityHit(atVisibility) && (atVisibility.t < t)) {
                at = own;
            }
        }

        // The frost: a disc of taps whose radius is the roughness's share of the spread over the distance behind the
        // glass, in render pixels at the opaque hit's depth.
        float radius = min(((roughness * GlassFrostSpread * behind) / max((footprint * opaque), 1.0e-4)), GlassFrostMaxPixels);
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

        float cosine = saturate(dot(normal, -rayDirection));
        float ratio = ((ior - 1.0) / (ior + 1.0));
        float f0 = (ratio * ratio);
        float fresnel = (f0 + ((1.0 - f0) * pow((1.0 - cosine), 5.0)));
        float air = sdfAirTransmittance(origin, rayDirection, t);

        // The GGX lobe reads the glass's own reflectance and nothing of a metal or a coat; the flow's revealed surface
        // covers the glass rather than roughening it.
        material.specular = f0;
        material.metal = 0.0;
        material.coat = 0.0;
        material.roughness = roughness;

        bool probed = sdfMeshIrradianceEnabled();
        SdfMeshProbes probes = sdfMeshProbesAt(surfacePoint, normal);
        float3 highlights;
        float incoming;
        float3 received = (sdfGlassReceived(material, surfacePoint, normal, rayDirection, probed, probes, highlights, incoming) * sdfLocalAdaptationGain(incoming));
        float3 reflectDirection = reflect(rayDirection, normal);
        float3 environment = worldEnvironmentReflection(reflectDirection, roughness);

        if (probed && (probes.share > 0.0)) {
            environment = sdfMeshReflection(probes, reflectDirection, environment);
        }

        float3 shaded = ((((environment * fresnel) + highlights) + (((1.0 - fresnel) * medium) * received)) * air) + (((1.0 - fresnel) * transmittance) * through.rgb);
        float luminous = dot(transmittance, SDF_LUMINANCE);
        float coverage = (1.0 - ((1.0 - through.a) * ((1.0 - fresnel) * luminous)));

        color = float4(lerp(shaded, ((material.flowColor.rgb * received) * air), revealed), lerp(coverage, 1.0, revealed));
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
