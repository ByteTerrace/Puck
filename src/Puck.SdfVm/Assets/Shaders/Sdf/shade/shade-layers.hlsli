// Generic shading layers. Coordinates arrive in the winning instance frame.
#ifndef SDF_SHADE_LAYERS_HLSLI
#define SDF_SHADE_LAYERS_HLSLI
// Draws a material's paints (SdfPaint), each over the one before. A paint's shape, evaluated at the hit through the one
// shape dispatch (evaluateShape) and never through the field, gives the albedo the paint's colour inside the shape, or
// within its stroke of the shape's surface, the edge antialiased over `footprint`, the world size of the pixel at the
// hit. `worldPoint` is the hit in world space and `layerPoint` in the winning frame; a paint riding a slot reads that
// slot's frame. A hit outside the shape's bound box, widened by the stroke and the pixel, costs one rotation. It reads
// and writes the albedo alone (sdfMaterialPaints), so the caller loads the rest of the material after it: a material
// live across the shape dispatch held the views pass at 168 registers on Direct3D 12. KEEP IN SYNC with
// SdfProgram.PackPaint.
void sdfApplyPaints(float3 worldPoint, float3 layerPoint, float footprint, uint paintFirst, uint paintCount, inout float3 albedo) {
    uint count = min(paintCount, SDF_MAX_PAINTS_PER_MATERIAL);
    if ((count == 0u) || !sdfProgramRange(paintFirst, count, SDF_PAINT_VECTORS_PER_ENTRY)) return;
    float edge = max((0.5 * footprint), 1.0e-6);

    [loop] for (uint i = 0u; i < count; i++) {
        uint base = (paintFirst + (i * SDF_PAINT_VECTORS_PER_ENTRY));
        float4 frame = asfloat(sdfWords[base]);
        float4 colorStroke = asfloat(sdfWords[base + 5u]);
        float4 slotCenter = asfloat(sdfWords[base + 6u]);
        uint slot = asuint(slotCenter.x);
        float3 p = layerPoint;

        if (slot != SDF_PAINT_WINNING_FRAME) {
            float4 slotRotation = sdfDynamicTransformRow((3u * slot) + 1u);
            if (dot(slotRotation, slotRotation) <= 0.0) continue;
            p = rotatePointByInverseQuaternion((worldPoint - sdfDynamicTransformRow(3u * slot).xyz), slotRotation);
        }

        float3 local = rotatePointByInverseQuaternion((p - frame.xyz), asfloat(sdfWords[base + 1u]));
        if (any(abs(local - slotCenter.yzw) > (asfloat(sdfWords[base + 7u]).xyz + (colorStroke.w + footprint)))) continue;
        float4 scale = asfloat(sdfWords[base + 2u]);
        float d = (evaluateShape(asuint(frame.w), (local / scale.xyz), asfloat(sdfWords[base + 3u]), asfloat(sdfWords[base + 4u])) * scale.w);
        if (colorStroke.w > 0.0) d = (abs(d) - colorStroke.w);
        albedo = lerp(albedo, colorStroke.rgb, (1.0 - smoothstep(-edge, edge, d)));
    }
}
// Refract into the authored local plane, then sample its radial ramp. No view-facing frame is synthesized.
void applyInset(float3 position, float3 normal, float3 ray, inout SdfMaterialData material) {
    uint count = (uint)material.paintControls.y;
    if (count == 0u) return;
    float4 rotation = material.insetRotation;
    float3 p = rotatePointByInverseQuaternion(position - material.insetOriginDepth.xyz, rotation);
    float3 n = rotatePointByInverseQuaternion(normal, rotation);
    float3 d = rotatePointByInverseQuaternion(ray, rotation);
    if (dot(n, d) > 0.0) n = -n;
    d = refract(d, n, 1.0 / material.paintControls.x);
    if (dot(d, d) < 1.0e-12 || abs(d.z) < 1.0e-6) return;
    float t = (-material.insetOriginDepth.w - p.z) / d.z;
    if (t < 0.0) return;
    float2 uv = (p + t * d).xy;
    float radius = length(uv);
    float phase = (float)sdfPcg3d(uint3(asuint(material.paintModulation.y), 0u, 0u)).x * SDF_INV_2POW32 * 2.0 * SDF_PI;
    radius *= 1.0 + material.paintControls.w * sin(atan2(uv.y, uv.x) * material.paintModulation.x + phase);
    float3 color = material.paintStops[0].rgb;
    // Unrolled to the array's length and guarded, so every index is a constant: NVIDIA's Vulkan compiler keeps a
    // dynamically indexed local array in per-thread memory, and DXC's SPIR-V unroller keeps a loop with a break rolled.
    [unroll] for (uint i = 1u; i < 4u; i++) {
        if (i < count) {
            float4 left = material.paintStops[i - 1u];
            float4 right = material.paintStops[i];
            float width = max((right.w - left.w) * material.paintControls.z, 1.0e-6);
            float blend = smoothstep(right.w - width, right.w, radius);
            color = lerp(color, right.rgb, blend);
        }
    }
    material.albedo = color;
}
#endif
