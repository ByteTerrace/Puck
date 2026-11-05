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
// The value-noise lattice the weathering reads (sdfLatticeNoise3: the same corners, the quintic blend) with its analytic
// gradient: (value in [-1, 1], its derivative along each lattice axis).
float4 sdfFlowNoise(float3 q, uint seed) {
    uint3 seeds = sdfLatticeSeeds(seed);
    float3 cellFloor = floor(q);
    int3 cell = int3(cellFloor);
    float3 f = (q - cellFloor);
    float3 u = (((f * f) * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float3 du = (((30.0 * f) * f) * ((f * (f - 2.0)) + 1.0));
    float c000 = sdfNoiseCorner3(cell, seeds);
    float c100 = sdfNoiseCorner3((cell + int3(1, 0, 0)), seeds);
    float c010 = sdfNoiseCorner3((cell + int3(0, 1, 0)), seeds);
    float c110 = sdfNoiseCorner3((cell + int3(1, 1, 0)), seeds);
    float c001 = sdfNoiseCorner3((cell + int3(0, 0, 1)), seeds);
    float c101 = sdfNoiseCorner3((cell + int3(1, 0, 1)), seeds);
    float c011 = sdfNoiseCorner3((cell + int3(0, 1, 1)), seeds);
    float c111 = sdfNoiseCorner3((cell + int3(1, 1, 1)), seeds);
    float k1 = (c100 - c000);
    float k2 = (c010 - c000);
    float k3 = (c001 - c000);
    float k4 = (((c110 - c010) - c100) + c000);
    float k5 = (((c101 - c001) - c100) + c000);
    float k6 = (((c011 - c001) - c010) + c000);
    float k7 = (((((((c111 - c011) - c101) - c110) + c100) + c010) + c001) - c000);
    float value = (((c000 + (k1 * u.x)) + (k2 * u.y)) + (k3 * u.z)) + (((k4 * u.x) * u.y) + ((k5 * u.x) * u.z)) + (((k6 * u.y) * u.z) + (((k7 * u.x) * u.y) * u.z));
    float3 gradient = (du * float3(
        (((k1 + (k4 * u.y)) + (k5 * u.z)) + ((k7 * u.y) * u.z)),
        (((k2 + (k4 * u.x)) + (k6 * u.z)) + ((k7 * u.x) * u.z)),
        (((k3 + (k5 * u.x)) + (k6 * u.y)) + ((k7 * u.x) * u.y))));
    return (float4(value, gradient) * 2.0) - float4(1.0, 0.0, 0.0, 0.0);
}
// The flow layer (SdfFlow, the material's flow rows): the pattern's lookup point advects along the flow's tangential part
// by the flow clock, in two layers half a cycle apart whose triangle weights cross-fade so neither restart shows, each
// place's phase offset by a low-frequency noise so the surface never pulses as one, and the blend renormalized so its
// contrast holds through the cross-fade. The pattern reveals the flow's surface over its coverage and tilts the normal
// by its slope, scaled by the share of the flow that runs along the surface. `layerFlow` is the velocity in the layer
// frame the caller converted it to; `clock` is the pass block's flow clock (SdfFlow.Clock), the one way the phase enters;
// `footprint` the world size of the pixel at the hit, which fades unresolved pattern toward its mean as the weathering
// does. Returns the shading normal in the layer frame: `layerNormal` itself for a material with no flow or no tilt.
// KEEP IN SYNC with SdfProgram.PackMaterials's flow rows.
float3 sdfApplyFlow(float3 layerPoint, float3 layerNormal, float3 layerFlow, float clock, float footprint, float4 lanes, inout SdfMaterialData material) {
    float scale = material.flowVelocity.w;
    float4 shape = material.flowShape;
    if ((scale <= 0.0) || ((shape.z <= 0.0) && (shape.w <= 0.0))) return layerNormal;
    uint lane = (material.flowControl & SDF_FLOW_LANE_MASK);
    float3 flow = (layerFlow * ((lane != 0u) ? lanes[min(lane - 1u, 3u)] : 1.0));
    float3 tangent = (flow - (layerNormal * dot(layerNormal, flow)));
    float speed = length(tangent);
    float3 along = ((speed > 1.0e-6) ? (tangent / speed) : float3(0.0, 0.0, 0.0));
    float squeeze = (1.0 - (1.0 / max(shape.x, 1.0)));
    uint seed = material.flowSeed;
    float offset = (0.5 + (0.5 * sdfLatticeNoise3((layerPoint * (0.125 * scale)), (seed ^ 0x68E31DA4u))));
    float c0 = frac((clock * shape.y) + offset);
    float c1 = frac(c0 + 0.5);
    float w0 = (1.0 - abs((2.0 * c0) - 1.0));
    float w1 = (1.0 - w0);
    float3 travel = (tangent * (SDF_FLOW_CLOCK_PERIOD / shape.y));
    float3 q0 = ((layerPoint - (travel * (c0 - 0.5))) * scale);
    float3 q1 = (((layerPoint - (travel * (c1 - 0.5))) * scale) + float3(5.3, 2.9, 7.1));
    q0 -= (along * (dot(q0, along) * squeeze));
    q1 -= (along * (dot(q1, along) * squeeze));
    float4 pattern = (((w0 * sdfFlowNoise(q0, seed)) + (w1 * sdfFlowNoise(q1, seed))) * rsqrt(max(((w0 * w0) + (w1 * w1)), 1.0e-6)));
    float rate = (footprint * scale);
    float fade = (1.0 - smoothstep(0.35, 0.9, rate));
    if (shape.w > 0.0) {
        float threshold = (1.0 - shape.w);
        float soft = max(material.flowColor.w, 0.001);
        float reveal = smoothstep((threshold - soft), (threshold + soft), (0.5 + (0.5 * clamp(pattern.x, -1.0, 1.0))));
        sdfRevealSurface(material.flowColor.rgb, material.flowResponse, lerp(shape.w, reveal, fade), material);
    }
    if ((shape.z <= 0.0) || (speed <= 1.0e-6)) return layerNormal;
    float3 slope = (pattern.yzw * fade);
    slope -= (along * (dot(slope, along) * squeeze));
    slope -= (layerNormal * dot(layerNormal, slope));
    float share = saturate(speed / max(length(layerFlow), 1.0e-6));
    return normalize(layerNormal - (slope * (shape.z * share)));
}
#endif
