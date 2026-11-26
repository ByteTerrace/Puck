// The reflection record (SdfWorldPackage.ViewsReflections): what a render pixel's mirror ray reaches on screen, written by
// the reflection pass (passes/sdf-world-views-reflections.comp.hlsl) and blended by views over the irradiance volume's
// reflection (sdfViewsScreenReflection). Two words a pixel, indexed as the reactivity is (sdfReactivityIndex):
// 0: the colour the ray reached, red and green as halves, at the stored light scale (sdfStoredLightScale).
// 1: its blue as a half in the low bits; above it, as UNORM8, the share of the reflection the colour answers, then the
//    share the open sky answers. Shares summing to zero leave the volume's reflection whole.
#ifndef PASSES_SDF_VIEWS_REFLECTIONS_HLSLI
#define PASSES_SDF_VIEWS_REFLECTIONS_HLSLI

#if SDF_VIEWS_REFLECTION_WORDS != 2
#error The reflection record is one uint2 a pixel (SdfWorldPackage.ViewsReflectionWords).
#endif

uint2 sdfViewsReflectionPack(float3 color, float hit, float open) {
    uint2 hitOpen = (uint2)round(saturate(float2(hit, open)) * 255.0);

    return uint2((f32tof16(color.r) | (f32tof16(color.g) << 16u)), (f32tof16(color.b) | (hitOpen.x << 16u) | (hitOpen.y << 24u)));
}

#if !defined(SDF_VIEWS_REFLECTIONS_PASS)
// The environment a views pixel reflects: the volume's reflection (`fallback`, sdfViewsReflectionSeen) where the
// reflection pass found nothing, the colour its mirror ray reached on screen and the open sky (`sky`,
// worldEnvironmentReflection) by the shares its record holds, and the volume's alone in a view whose fragment holds no
// reflection pass (passGroup.screenReflections).
float3 sdfViewsScreenReflection(SdfPixel p, float3 sky, float3 fallback) {
    if (passGroup.screenReflections == 0u) {
        return fallback;
    }

    uint2 record = sdfViewsReflections[sdfReactivityIndex(p.pixel, p.viewIndex, passGroup.imageExtent)];
    float hit = (float((record.y >> 16u) & 0xFFu) / 255.0);
    float open = (float(record.y >> 24u) / 255.0);

    if ((hit + open) <= 0.0) {
        return fallback;
    }

    float3 color = (float3(f16tof32(record.x), f16tof32(record.x >> 16u), f16tof32(record.y)) / sdfStoredLightScale(passGroup.debugMode, passGroup.lightScale));

    return (((fallback * saturate(1.0 - (hit + open))) + (color * hit)) + (sky * open));
}
#endif

#endif
