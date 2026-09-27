// The tonemap (render.tonemap Filmic), the fragment stage of the sdf.tonemap render graph package: a world's root graph
// runs it over the placed views alone, before any pane, post pass or the overlay, so the scene is tonemapped once, a
// pane (display-referred, its own tonemap included) never is, and the HUD composes over the frame at SDR white. The
// display encode then quantizes the frame for the display (display-encode.frag.hlsl in Puck.Shaders). Reuses
// fullscreen.vert.hlsl, no vertex stage of its own.
//
// It reads its source image and sampler from the set's generated interface (sdf-tonemap.interface.hlsli, regenerated
// with `puck shaders generate`).
#include "sdf-tonemap.interface.hlsli"

// The Narkowicz ACES-fit filmic curve, and only the curve. The study follows it with a gamma-2.2 encode because its
// shading is linear light; this pipeline's stylized shading is already display-referred (the display encode writes it to
// an SDR display as it is), so a second encode here washes the whole frame out.
float3 sdfFilmicTonemap(float3 color) {
    return saturate((color * ((2.51 * color) + 0.03)) / (((color * ((2.43 * color) + 0.59)) + 0.14)));
}

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    uint width;
    uint height;

    source.GetDimensions(width, height);

    return float4(sdfFilmicTonemap(source.Sample(sourceSampler, (fragCoord.xy / float2(width, height))).rgb), 1.0);
}
