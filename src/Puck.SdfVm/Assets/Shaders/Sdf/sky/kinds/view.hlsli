// The view kind: an infinity view's image. The instance (another world, or far geometry, as a second sdf.world instance)
// renders exactly the rectangle of the viewer's camera plane its layer's mask covers, so the pixel's world direction, as
// the tangent it has on the viewer's basis the instance was fitted to, indexes the image directly: no sky frame, and no
// layer rotation. A direction behind the camera plane or outside the rectangle draws nothing. Each consumer supplies its
// own fitted records and image bindings, indexed by the packed layer ordinal; no authored screen index is allocated.
// The image is clamped half a source pixel inside its extent; an instance with no completed image draws the layer's
// fallback colour instead. Either way the evaluation counts one shown texel, which is what
// the host reads back to demand the instance's next frame; a pass that binds no infinity images (the environment map's) draws
// nothing and counts nothing. Opaque unless the layer is far geometry, whose image alpha is its coverage and whose RGB
// is premultiplied. Unpremultiply after filtering so the layer blend applies coverage once, including at edges. One evaluation
// and one texel a sample.
#ifndef SKY_KINDS_VIEW_HLSLI
#define SKY_KINDS_VIEW_HLSLI

float4 sdfSkyViewLayer(SdfSkyView view, SdfSkyLayer layer, SdfSkySample sample) {
#ifdef SDF_SKY_VIEWS
    // Reuse the generated parameter decoder over the per-consumer payload, preserving the authored header/detail row.
    uint row = (sample.layer * SDF_SKY_VIEW_ROWS);
    SdfSkyLayer fitted = layer;

    fitted.P0 = passGroup.skyViews[row];
    fitted.P1 = passGroup.skyViews[row + 1u];
    fitted.P2 = passGroup.skyViews[row + 2u];
    fitted.P3 = passGroup.skyViews[row + 3u];
    fitted.P4 = passGroup.skyViews[row + 4u];
    view = sdfSkyViewOf(fitted);
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

    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 1u);

    if ((view.ImageSlot < 0) || (view.ImageSlot >= (int)SDF_SKY_MAX_LAYERS)) {
        return float4((view.Fallback * view.Intensity), 1.0);
    }

    uint image = (uint)view.ImageSlot;
    uint width, height;

    skyViewImages[image].GetDimensions(width, height);
    float2 inset = (0.5 / float2(width, height));
    float2 source = clamp(uv, inset, (1.0 - inset));
    float4 sampled = skyViewImages[image].SampleLevel(samplers[SDF_FILTER_LINEAR], source, 0.0);

    if (view.Coverage != 0u) {
        float3 color = ((sampled.a > 0.0) ? (sampled.rgb / sampled.a) : float3(0.0, 0.0, 0.0));

        return float4((color * view.Intensity), sampled.a);
    }
    return float4((sampled.rgb * view.Intensity), 1.0);
#else
    return float4(0.0, 0.0, 0.0, 0.0);
#endif
}
#endif
