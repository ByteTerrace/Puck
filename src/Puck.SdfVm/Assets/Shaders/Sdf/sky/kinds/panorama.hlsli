// The panorama kind: the image a diegetic screen shows, sampled by the layer-frame direction. Equirectangular maps
// longitude across and latitude down, the image's top row the zenith; octahedral is the environment map's projection,
// the pole at the zenith. The point is mapped into the screen's crop and clamped half a source pixel inside it
// (frame/sdf-environment.hlsli's mapping), and read through the sampler the screen's row names. A pass that binds no
// screens (SDF_SKY_SCREENS undefined: the environment map's) and a screen with no source bound this frame draw nothing.
// The screen index is the layer table's, uniform across a dispatch, so it indexes the screens array directly. One
// evaluation and one texture load a sample. Opaque.
#ifndef SKY_KINDS_PANORAMA_HLSLI
#define SKY_KINDS_PANORAMA_HLSLI

float4 sdfSkyPanoramaLayer(SdfSkyPanorama panorama, SdfSkyLayer layer, SdfSkySample sample) {
#ifdef SDF_SKY_SCREENS
    if ((panorama.Screen < 0) || (panorama.Screen >= (int)SDF_MAX_SCREEN_SURFACES)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    uint screen = (uint)panorama.Screen;
    ScreenMappingData mapping = worldScreenMapping(screen);

    if (mapping.state.x == 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float3 direction = sample.local;
    float2 uv;

    if (panorama.Projection == SDF_SKY_PROJECTION_OCTAHEDRAL) {
        uv = ((sdfOctEncode(float3(direction.x, direction.z, direction.y)) * 0.5) + 0.5);
    } else {
        uv = float2(((atan2(direction.x, -direction.z) * 0.15915494) + 0.5), (acos(clamp(direction.y, -1.0, 1.0)) * 0.31830989));
    }

    float2 source = clamp(lerp(mapping.crop.xy, mapping.crop.zw, uv), mapping.sampleClamp.xy, mapping.sampleClamp.zw);
    float4 sampled = screenSources[screen].SampleLevel(samplers[(uint)mapping.state.y], source, 0.0);

    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 1u);

    return float4((sampled.rgb * panorama.Intensity), 1.0);
#else
    return float4(0.0, 0.0, 0.0, 0.0);
#endif
}
#endif
