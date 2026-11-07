namespace Puck.SdfVm;

public static partial class SdfShadeHlsl {
    // Generated once with the interpreter. Both opaque and glass callers supply this same stamp and use these
    // phase doors; neither can omit sanitization or invent a second register/stamp convention.
    private const string Invocation = """
uint4 sdfShadeBinding(int material) {
    uint address;
    if (passGroup.shadePrograms == 0u || !sdfMaterialBase(material, address)) return 0u;
    uint4 own = asuint(sdfWords[address + 32u]);
    return own.y != 0u ? own : passGroup.shadeDefault;
}
bool sdfShadeHas(uint4 binding, uint phase) { return binding.y != 0u && sdfShadeRead(binding.xy, phase).y != 0u; }
float3 sdfShadeDiagnostic(int material, int mode) {
    uint4 binding = sdfShadeBinding(material);
    if (mode == SDF_DEBUG_VIEW_SHADE_TIER) return binding.y != 0u ? float3(0.0, 0.8, 1.0) : 0.15;
    float work = float(sdfShadeRead(binding.xy, 0u).z + sdfShadeRead(binding.xy, 1u).z + sdfShadeRead(binding.xy, 2u).z) / float(SDF_SHADE_MAX_OPS);
    return float3(work, work * work, 0.0);
}
SdfShadeStamp sdfShadeStamp(SdfMaterialData material, float3 position, float3 layerPosition, float3 normal,
    float3 ray, float curvature, float footprint, float4 lanes, float contact, float thickness, float4 rotation, int frameSlot, uint draw) {
    SdfShadeStamp stamp = (SdfShadeStamp)0;
    stamp.layerRotation = rotation;
    uint record = sdfMeshRecord(draw);
    stamp.drawDirection = frameSlot != SDF_TRANSFORM_SLOT_NONE ? float3x3(1,0,0,0,1,0,0,0,1) : transpose(float3x3(
        sdfMeshCarryDirection(record, sdfMeshWorldDirection(record, float3(1,0,0))),
        sdfMeshCarryDirection(record, sdfMeshWorldDirection(record, float3(0,1,0))),
        sdfMeshCarryDirection(record, sdfMeshWorldDirection(record, float3(0,0,1)))));
    sdfShadeSet_surface_position(stamp, position);
    sdfShadeSet_surface_layerPosition(stamp, layerPosition);
    sdfShadeSet_surface_geometricNormal(stamp, normal);
    sdfShadeSet_surface_normal(stamp, normal);
    sdfShadeSet_surface_curvature(stamp, curvature);
    sdfShadeSet_surface_footprint(stamp, footprint);
    sdfShadeSet_surface_rayDirection(stamp, ray);
    sdfShadeSet_surface_contact(stamp, contact);
    sdfShadeSet_surface_thickness(stamp, thickness);
    sdfShadeSet_clock(stamp, passGroup.flowClock);
    sdfShadeSet_material_albedo(stamp, material.albedo);
    sdfShadeSet_material_roughness(stamp, material.roughness);
    sdfShadeSet_material_metal(stamp, material.metal);
    sdfShadeSet_material_specular(stamp, material.specular);
    sdfShadeSet_material_coat(stamp, material.coat);
    sdfShadeSet_material_sheen(stamp, material.sheen);
    sdfShadeSet_material_wrap(stamp, material.wrap);
    sdfShadeSet_material_fill(stamp, material.fill);
    sdfShadeSet_material_emissive(stamp, material.emissive);
    sdfShadeSet_lanes_slot0(stamp, sdfLane(lanes, 0u));
    sdfShadeSet_lanes_slot1(stamp, sdfLane(lanes, 1u));
    sdfShadeSet_lanes_slot2(stamp, sdfLane(lanes, 2u));
    sdfShadeSet_lanes_slot3(stamp, sdfLane(lanes, 3u));
    sdfShadeSet_lanes_world0(stamp, sdfLane(lanes, 4u));
    sdfShadeSet_lanes_world1(stamp, sdfLane(lanes, 5u));
    sdfShadeSet_lanes_world2(stamp, sdfLane(lanes, 6u));
    sdfShadeSet_lanes_world3(stamp, sdfLane(lanes, 7u));
    return stamp;
}
void sdfShadeSurfaceInputs(inout SdfShadeStamp stamp, SdfMaterialData material, float3 normal, float reveal) {
    sdfShadeSet_surface_normal(stamp, normal);
    sdfShadeSet_surface_albedo(stamp, material.albedo);
    sdfShadeSet_surface_roughness(stamp, material.roughness);
    sdfShadeSet_surface_metal(stamp, material.metal);
    sdfShadeSet_surface_specular(stamp, material.specular);
    sdfShadeSet_surface_coat(stamp, material.coat);
    sdfShadeSet_surface_sheen(stamp, material.sheen);
    sdfShadeSet_surface_wrap(stamp, material.wrap);
    sdfShadeSet_surface_fill(stamp, material.fill);
    sdfShadeSet_surface_emissive(stamp, material.emissive);
    sdfShadeSet_surface_reveal(stamp, reveal);
}
float sdfShadeUnit(uint4 value, float fallback) { return sdfShadeSanitize(asfloat(value), fallback.xxxx, 0.0, 1.0).x; }
float3 sdfShadeColor(uint4 value, float3 fallback) { return sdfShadeSanitize(asfloat(value), float4(fallback,0), 0.0, 3.402823466e+38).xyz; }
void sdfShadeSurfacePhase(uint4 binding, inout SdfShadeStamp stamp, inout SdfMaterialData material, inout float3 normal, out float reveal) {
    reveal = 0.0;
    if (sdfShadeHas(binding, 0u)) {
        SdfShadeOutputs o;
        sdfShadeInterpret(binding.xy, binding.zw, 0u, stamp, o);
        if ((o.assigned & 1u) != 0u) material.albedo = sdfShadeColor(o.o0, material.albedo);
        if ((o.assigned & 2u) != 0u) material.roughness = sdfShadeUnit(o.o1, material.roughness);
        if ((o.assigned & 4u) != 0u) material.metal = sdfShadeUnit(o.o2, material.metal);
        if ((o.assigned & 8u) != 0u) material.specular = sdfShadeColor(o.o3, material.specular.xxx).x;
        if ((o.assigned & 16u) != 0u) material.coat = sdfShadeUnit(o.o4, material.coat);
        if ((o.assigned & 32u) != 0u) material.sheen = sdfShadeUnit(o.o5, material.sheen);
        if ((o.assigned & 64u) != 0u) material.wrap = sdfShadeUnit(o.o6, material.wrap);
        if ((o.assigned & 128u) != 0u) material.fill = sdfShadeColor(o.o7, material.fill);
        if ((o.assigned & 256u) != 0u) material.emissive = sdfShadeColor(o.o8, material.emissive.xxx).x;
        if ((o.assigned & 512u) != 0u) normal = sdfShadeSanitizeNormal(asfloat(o.o9).xyz, normal);
        if ((o.assigned & 1024u) != 0u) reveal = sdfShadeUnit(o.o10, 0.0);
    }
    sdfShadeSurfaceInputs(stamp, material, normal, reveal);
}
SdfLightResponse sdfShadeLightPhase(uint4 binding, SdfShadeStamp stamp, SdfLightSource light, SdfShadeSurface surface) {
    SdfLightResponse response = sdfLightResponse(light, surface);
    if (!sdfShadeHas(binding, 1u)) return response;
    float3 direction = light.position;
    float reach = 1.0;
    float probeShare = surface.probed && light.record.Shadows != 0u && light.index >= 0 ? sdfMeshLightVisibility(surface.probes, (uint)light.index) : 1.0;
    float visibility = sdfLightVisibility(light.index, surface.shadowVisibility, surface.ambientOcclusion, surface.shadowMeasured);
    if (!surface.cascaded && surface.probed && light.record.Shadows != 0u && light.index >= 0) visibility = probeShare;
    if (light.kind == SDF_LIGHT_POINT) reach = sdfPointReach(light.record, light.position, surface.position, direction);
    else if (light.kind != SDF_LIGHT_DIRECTIONAL) {
        float3 delta = light.position - surface.position;
        float distanceSquared = dot(delta, delta);
        direction = delta / sqrt(max(distanceSquared, 1.0e-8));
        if (light.kind == SdfLightScreen) reach = saturate(dot(light.facing, -direction)) / (distanceSquared + light.weight);
    }
    sdfShadeSet_light_direction(stamp, direction);
    sdfShadeSet_light_position(stamp, light.position);
    sdfShadeSet_light_color(stamp, light.color);
    sdfShadeSet_light_weight(stamp, light.weight);
    sdfShadeSet_light_kind(stamp, light.kind);
    sdfShadeSet_light_reach(stamp, reach);
    sdfShadeSet_light_visibility(stamp, visibility);
    sdfShadeSet_light_occlusion(stamp, surface.ambientOcclusion);
    sdfShadeSet_light_probeShare(stamp, probeShare);
    sdfShadeSet_light_cosine(stamp, dot(surface.normal, direction));
    SdfShadeOutputs o;
    sdfShadeInterpret(binding.xy, binding.zw, 1u, stamp, o);
    if ((o.assigned & 1u) != 0u) response.diffuse = sdfShadeColor(o.o0, response.diffuse);
    if ((o.assigned & 2u) != 0u) response.rim = sdfShadeColor(o.o1, response.rim);
    if ((o.assigned & 4u) != 0u) response.specular = sdfShadeColor(o.o2, response.specular);
    if ((o.assigned & 8u) != 0u) response.attenuation = sdfShadeUnit(o.o3, response.attenuation);
    return response;
}
float3 sdfShadeComposePhase(uint4 binding, SdfShadeStamp stamp, float3 radiance, float3 rim, float3 specular,
    float attenuation, float3 indirect, float3 reflection, float3 environment, float adaptation, float3 fallback) {
    if (!sdfShadeHas(binding, 2u)) return fallback;
    sdfShadeSet_compose_radiance(stamp, radiance);
    sdfShadeSet_compose_rim(stamp, rim);
    sdfShadeSet_compose_specular(stamp, specular);
    sdfShadeSet_compose_attenuation(stamp, attenuation);
    sdfShadeSet_compose_indirect(stamp, indirect);
    sdfShadeSet_compose_reflection(stamp, reflection);
    sdfShadeSet_compose_environment(stamp, environment);
    sdfShadeSet_compose_adaptation(stamp, adaptation);
    sdfShadeSet_compose_exposure(stamp, passGroup.exposure);
    SdfShadeOutputs o;
    sdfShadeInterpret(binding.xy, binding.zw, 2u, stamp, o);
    return (o.assigned & 1u) != 0u ? sdfShadeColor(o.o0, fallback) : fallback;
}
""";
}
