// Cel material response in scene luminance. Screens and sky never call this module.
#ifndef SHADE_SDF_TOON_HLSLI
#define SHADE_SDF_TOON_HLSLI

bool sdfToonEnabled() { return passGroup.toonBands.x > 0.0; }

float sdfToonBand(float cosine, float visibility) {
    float halfWidth = max(passGroup.toonBands.z * 0.5, 0.0005);
    return smoothstep(passGroup.toonBands.y - halfWidth, passGroup.toonBands.y + halfWidth,
        saturate(cosine) * saturate(visibility));
}

float3 sdfToonSpecular(SdfMaterialData material, float3 normal, float3 view, float3 light, float radiance, float visibility) {
    float3 halfway = light + view;
    float lengthSquared = dot(halfway, halfway);
    float cosine = dot(normal, halfway * rsqrt(max(lengthSquared, 1.0e-8)));
    float size = passGroup.toonBands.w * lerp(0.5, 1.5, material.roughness);
    // A narrow analytic edge survives subpixel movement without an unbounded GGX peak.
    float highlight = smoothstep(1.0 - size, 1.0 - size + min(size * 0.15, 0.003), cosine);
    float3 reflectance = lerp(material.specular.xxx, material.albedo, material.metal);
    float gain = (1.0 - 0.75 * material.roughness) * 2.0;
    return reflectance * (highlight * gain * radiance * sdfToonBand(dot(normal, light), visibility));
}

// Compress all channels by the same factor, preserving paint chroma. Emission is added afterwards.
float3 sdfToonExpose(float3 reflected) {
    float3 exposed = max(reflected, 0.0) * (passGroup.exposure * passGroup.toonShadow.w);
    float peak = max(exposed.r, max(exposed.g, exposed.b));
    return exposed / ((1.0 + peak * passGroup.toonOutline.w) * max(passGroup.exposure, 1.0e-12));
}

#endif
