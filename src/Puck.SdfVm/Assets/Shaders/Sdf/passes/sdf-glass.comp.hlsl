// The glass shading pass, one invocation a pixel of the render grid, after views and before the sky, the resolve and the
// composite: writes the lit image views wrote over again, every pixel copied as it stands but one the glass target holds a
// front face at before its opaque hit. Such a pixel is shaded as glass (SdfWorldPackage.WithGlass):
// - its surface is the glass draw's triangle at the ray's hit, normal and material resolved as the surface pass resolves
//   an opaque hit (sdfMeshSurfaceNormal, sdfMeshMaterial), its index of refraction and absorption the material's
//   (sdfMaterialLoad) and its frost the material's roughness;
// - the distance the ray travels through glass is the glass sums' (frame/sdf-glass.hlsli), so a hollow shell absorbs
//   over both its walls and a solid holding an opaque object over the glass before it, and the light through it is the
//   lit image behind, absorbed over that distance by Beer–Lambert (exp(-absorption * distance));
// - that light is read where the ray bent by Snell's law leaves the glass, carried on parallel to the pixel's ray to the
//   opaque hit's depth and projected back to the render grid, so what lies behind the glass shifts as it does through a
//   slab, and read at the pixel's own sample where that lands on a nearer surface; frost spreads the read over a disc of
//   taps whose radius grows with the roughness and the distance from the glass to what lies behind, turned by a
//   per-pixel pattern that never moves between frames;
// - the sky reflects in it by Fresnel (Schlick over the index's normal reflectance), read from the residency's
//   environment map under the sky's reflection gain, and each light of the lights table adds its highlight through the
//   one GGX lobe (sdfMaterialSpecular), both through the atmosphere's transmittance over the glass's own distance; the
//   light through the glass carries the atmosphere over the opaque hit's distance already.
// The glass pixel's coverage is the light behind's, darkened by the glass's luminous transmittance, so the sky the
// composite puts behind it shows through a clear pane at the pixel's own direction, unrefracted and untinted but
// darkened. In a temporal view the pixel's reactivity is raised to one where the refracted read comes from a pixel
// away or more, since the resolve's history follows the opaque hit the record names, not what the glass shows; a flat
// pane bending nothing keeps its history and its edges resolve like any other. A debug view's lit image passes through.
// Glass seen through glass shades as the opaque image behind the nearer pane: the farther pane is the lit image's
// opaque pixels, never shaded as glass. The eye inside a glass solid sees no front face, so the pixel passes through.
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
#include "../shade/sdf-sky-environment.hlsli"
#include "../shade/sdf-transport.hlsli"

// The frost's taps and the most render pixels their disc spans.
static const uint GlassFrostTaps = 8u;
static const float GlassFrostMaxPixels = 24.0;
// The scatter a fully frosted glass spreads light over, as a fraction of the distance behind it.
static const float GlassFrostSpread = 0.25;
static const float GlassGoldenAngle = 2.39996323;
static const float3 GlassLuma = float3(0.2126, 0.7152, 0.0722);

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
// The sky in a direction from the residency's environment map, the layers the lighting sees, under the sky's reflection
// gain.
float3 sdfGlassSky(float3 direction) {
    if (sdfSky[0].Reflection <= 0.0) {
        return float3(0.0, 0.0, 0.0);
    }

    uint taps[4];
    float weights[4];
    float3 color = float3(0.0, 0.0, 0.0);

    sdfSkyEnvironmentTaps(direction, taps, weights);
    [unroll] for (uint i = 0u; i < 4u; i++) {
        color += (weights[i] * sdfSkyEnvironmentUnpack(sdfSkyEnvironment[taps[i]]));
    }

    return (color * sdfSky[0].Reflection);
}
// The lights' highlights on the glass: each directional's and point's GGX lobe through the glass's own Fresnel
// reflectance, unshadowed, since the glass casts into no cascade and reads none.
float3 sdfGlassHighlights(SdfMaterialData material, float3 position, float3 normal, float3 rayDirection) {
    float3 highlights = float3(0.0, 0.0, 0.0);
    uint count = worldLightCount();

    [loop]
    for (uint index = 0u; (index < count); index++) {
        SdfLight light = sdfLights[index];

        if (light.Kind == SDF_LIGHT_DIRECTIONAL) {
            highlights += (light.Color * sdfMaterialSpecular(material, normal, -rayDirection, light.Direction, light.Weight));
        } else if (light.Kind == SDF_LIGHT_POINT) {
            float3 toLight = (worldPointLightPosition(light) - position);
            float pointDistance = length(toLight);
            float3 pointDirection = (toLight / max(pointDistance, 1.0e-4));
            float pointRatio = (pointDistance / max(light.Param, 1.0e-3));
            float pointFalloff = (light.Weight / (1.0 + (pointRatio * pointRatio)));

            highlights += (light.Color * sdfMaterialSpecular(material, normal, -rayDirection, pointDirection, pointFalloff));
        }
    }

    return highlights;
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
    bool glass = ((passGroup.debugMode == 0u) && (front.y >= 1.0) && (front.x < opaque));
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
        uint word = sdfGlassSumWord(pixel, extent);
        float thickness = sdfGlassThickness(asint(sdfGlassSums[word]), asint(sdfGlassSums[(word + 1u)]), t, opaque);
        float3 transmittance = exp(-(material.absorption * thickness));

        // The ray bent into the glass leaves it `thickness` along, then carries on parallel to the pixel's ray to the
        // opaque hit's depth: where that lands on the render grid is where the light through the glass is read.
        float3 refracted = refract(rayDirection, normal, (1.0 / ior));
        float behind = max(((opaque - t) - thickness), 0.0);
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
        float footprint = ((2.0 * view.right.w) / max(float(extent.y), 1.0));
        float radius = min(((material.roughness * GlassFrostSpread * behind) / max((footprint * opaque), 1.0e-4)), GlassFrostMaxPixels);
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

        // The GGX lobe reads the glass's own reflectance and nothing of a metal or a coat.
        material.specular = f0;
        material.metal = 0.0;
        material.coat = 0.0;

        float3 reflection = ((sdfGlassSky(reflect(rayDirection, normal)) * fresnel) + sdfGlassHighlights(material, surfacePoint, normal, rayDirection));
        float3 passed = (((1.0 - fresnel) * transmittance) * through.rgb);
        float luminous = dot(transmittance, GlassLuma);

        color = float4(((reflection * air) + passed), (1.0 - ((1.0 - through.a) * ((1.0 - fresnel) * luminous))));
        reactive = saturate(length(at - own));
    }

    output[pixel] = color;
    if ((passGroup.temporal != 0u) && glass) {
        uint index = sdfReactivityIndex(pixel, 0u, extent);

        reactivityRW[index] = max(reactivityRW[index], reactive);
    }
    sdfWorkTexels = 1u;
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
