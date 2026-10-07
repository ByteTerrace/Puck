// The disc kind: a body's disc about its light's world direction (host-baked, SdfSky.Pack), which ignores the sky frame
// and the layer's rotation: it sits where its light shines from. Without a screen it is a pow(cos angle, k) glow whose
// edge, at the authored angular radius, is half bright (k host-baked from the radius). With a screen it is the texture
// body shape: the image that screen shows drawn across the disc, the disc's own tangent plane mapped onto the screen's
// crop (frame/sdf-environment.hlsli's mapping), with an edge a fiftieth of the radius wide; a pass that binds no screens
// and a screen with no source draw nothing. Its colour is Color times Intensity (times the image) and its alpha the glow
// or the disc's coverage, so an add blend lights the sky beneath. One evaluation a sample, and one texture load for a
// textured disc.
#ifndef SKY_KINDS_DISC_HLSLI
#define SKY_KINDS_DISC_HLSLI

float4 sdfSkyDiscLayer(SdfSkyDisc disc, SdfSkyLayer layer, SdfSkySample sample) {
    if (disc.Intensity <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float cosine = dot(sample.world, disc.Direction);

    if (disc.Screen < 0) {
        sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 0u);

        return float4((disc.Color * disc.Intensity), pow(saturate(cosine), disc.Exponent));
    }
#ifdef SDF_SKY_SCREENS
    if ((cosine <= 0.0) || (disc.Screen >= (int)SDF_MAX_SCREEN_SURFACES)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    uint screen = (uint)disc.Screen;
    ScreenMappingData mapping = worldScreenMapping(screen);

    if (mapping.state.x == 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // The disc's tangent plane: right is the world up × the direction (or +x when the direction is vertical), up completes
    // the basis, so the image stands upright about its centre.
    float3 axis = disc.Direction;
    float3 right = cross(float3(0.0, 1.0, 0.0), axis);

    right = ((dot(right, right) > 1.0e-8) ? normalize(right) : float3(1.0, 0.0, 0.0));

    float3 up = cross(axis, right);
    float reach = tan(min(disc.Radius, 1.5));
    float2 plane = (float2(dot(sample.world, right), dot(sample.world, up)) / (cosine * max(reach, 1.0e-6)));
    float distance = length(plane);
    float coverage = (1.0 - smoothstep(0.98, 1.0, distance));

    if (coverage <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float2 uv = float2(((plane.x * 0.5) + 0.5), (0.5 - (plane.y * 0.5)));
    float2 source = clamp(lerp(mapping.crop.xy, mapping.crop.zw, uv), mapping.sampleClamp.xy, mapping.sampleClamp.zw);
    float4 sampled = screenSources[screen].SampleLevel(samplers[(uint)mapping.state.y], source, 0.0);

    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 1u);

    return float4(((sampled.rgb * disc.Color) * disc.Intensity), coverage);
#else
    return float4(0.0, 0.0, 0.0, 0.0);
#endif
}
#endif
