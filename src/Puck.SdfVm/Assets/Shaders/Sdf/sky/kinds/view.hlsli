// The view kind: an infinity view's image. The instance (another world, or far geometry, as a second sdf.world instance)
// renders exactly the rectangle of the viewer's camera plane its layer's mask covers, so the pixel's world direction, as
// the tangent it has on the viewer's basis the instance was fitted to, indexes the image directly: no sky frame, and no
// layer rotation. A direction behind the camera plane or outside the rectangle draws nothing. The image is the screen the
// binder routed the instance to, read through that screen's mapping and sampler (frame/sdf-environment.hlsli's mapping),
// clamped half a source pixel inside the crop; a screen with no image this frame (the instance has not rendered yet, or
// is retired) draws the layer's fallback colour instead. Either way the evaluation counts one shown texel, which is what
// the host reads back to demand the instance's next frame; a pass that binds no screens (the environment map's) draws
// nothing and counts nothing. Opaque unless the layer is far geometry, whose image alpha is its coverage. One evaluation
// and one texel a sample.
#ifndef SKY_KINDS_VIEW_HLSLI
#define SKY_KINDS_VIEW_HLSLI

float4 sdfSkyViewLayer(SdfSkyView view, SdfSkyLayer layer, SdfSkySample sample) {
#ifdef SDF_SKY_SCREENS
    if (view.Intensity <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float forward = dot(sample.world, view.Forward);

    if (forward <= 0.0) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float2 tangent = (float2(dot(sample.world, view.Right), dot(sample.world, view.Up)) / forward);
    float2 span = (view.Rect.zw - view.Rect.xy);

    if ((span.x <= 0.0) || (span.y <= 0.0)) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float2 uv = ((tangent - view.Rect.xy) / span);

    if (any(uv < float2(0.0, 0.0)) || any(uv > float2(1.0, 1.0))) {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    // The image's rows run down, the rectangle's tangent runs up.
    uv.y = (1.0 - uv.y);

    bool shown = false;
    ScreenMappingData mapping = (ScreenMappingData)0;

    if ((view.Screen >= 0) && (view.Screen < (int)SDF_MAX_SCREEN_SURFACES)) {
        mapping = worldScreenMapping((uint)view.Screen);
        shown = (mapping.state.x != 0.0);
    }

    puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 1u);

    if (!shown) {
        return float4((view.Fallback * view.Intensity), 1.0);
    }

    uint screen = (uint)view.Screen;
    float2 source = clamp(lerp(mapping.crop.xy, mapping.crop.zw, uv), mapping.sampleClamp.xy, mapping.sampleClamp.zw);
    float4 sampled = screenSources[screen].SampleLevel(samplers[(uint)mapping.state.y], source, 0.0);

    return float4((sampled.rgb * view.Intensity), ((view.Coverage != 0u) ? sampled.a : 1.0));
#else
    return float4(0.0, 0.0, 0.0, 0.0);
#endif
}
#endif
