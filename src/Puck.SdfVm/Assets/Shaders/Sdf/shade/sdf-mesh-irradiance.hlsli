// The baked-mesh path's indirect light (views only): the irradiance volume SdfMeshIrradiance traces on the CPU against the
// static meshes, read at a shaded point by the eight probes about it. It performs no field query. The probe record and the
// normal bias are SdfMeshIrradianceProbe's (Puck.SdfVm.Model), generated into sdf-render.hlsli (SDF_MESH_IRRADIANCE_*); the
// key, the toroidal slot and the probe weights are mirrored by SdfMeshIrradiance's KeyOf, SlotOf and Incoming, the CPU
// trace's read of the same volume.
#ifndef SHADE_SDF_MESH_IRRADIANCE_HLSLI
#define SHADE_SDF_MESH_IRRADIANCE_HLSLI
#include "../isa/sdf-render.hlsli"
#include "../field/sdf-octahedral.hlsli"

bool sdfMeshIrradianceEnabled() {
    return (passGroup.meshIrradianceVolume.x != 0u);
}
uint sdfMeshIrradianceKey(int3 cell) {
    uint3 low = (asuint(cell) & 1023u);
    return (low.x | (low.y << 10) | (low.z << 20));
}
// The cell's coordinates modulo the volume's size, from 0, wrapped unsigned after a lift by 2^16 sizes, which covers every
// cell within 2^16 volumes of the origin: NVIDIA's Vulkan driver answers a signed remainder (OpSRem) of a negative operand
// wrongly, where Direct3D 12 answers it right.
uint sdfMeshIrradianceSlot(int3 cell, int3 dims) {
    uint3 size = (uint3)dims;
    uint3 wrapped = ((uint3)(cell + (dims * 65536)) % size);
    return (wrapped.x + (size.x * (wrapped.y + (size.y * wrapped.z))));
}
float sdfMeshIrradianceHalf(uint word, uint high) {
    return f16tof32((high != 0u) ? (word >> 16) : (word & 0xffffu));
}
// The ambient cube's value about a normal: each axis's face toward the normal, weighted by the normal's squared component.
// A probe's sky visibility, then its radiance, both read from its slot's words.
void sdfMeshIrradianceProbe(uint address, float3 normal, out float sky, out float3 radiance) {
    float3 squared = (normal * normal);
    sky = 0.0;
    radiance = 0.0;
    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        uint negative = ((normal[axis] < 0.0) ? 1u : 0u);
        uint face = ((2u * axis) + negative);
        sky += (squared[axis] * sdfMeshIrradianceHalf(sdfMeshIrradiance[address + 1u + axis], negative));
        float3 value;
        [unroll] for (uint channel = 0u; (channel < 3u); channel++) {
            uint entry = ((3u * face) + channel);
            value[channel] = sdfMeshIrradianceHalf(sdfMeshIrradiance[address + 4u + (entry >> 1)], (entry & 1u));
        }
        radiance += (squared[axis] * value);
    }
}

// KEEP IN SYNC with SdfMeshIrradiance.Gather. A corner's weight is its cell's trilinear share times
// max(0, dot(receiver normal, direction to relocated probe)) squared times depth-moment visibility.
// Continuous hemisphere weighting excludes probes behind a surface without a discrete front/back fallback.
// Unwritten, invalid and invisible probes take no part; no answering probe means no measured indirect light.
static const uint SdfMeshProbeNone = 0xffffffffu;

// Where the probe stands, relative to its cell's centre: the offset the trace moved it by out of geometry or off a
// face it lay on, zero for a probe at its centre. Every position the gather reads is the centre plus this.
// KEEP IN SYNC with SdfMeshIrradiance.Compose's offset words.
float3 sdfMeshProbeOffset(uint address) {
    uint first = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD];
    uint second = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD + 1u];

    return float3(sdfMeshIrradianceHalf(first, 0u), sdfMeshIrradianceHalf(first, 1u), sdfMeshIrradianceHalf(second, 0u));
}
// Octahedral depth moments: four bilinear taps, reflected at identified edges, and a one-sided Chebyshev bound.
// KEEP IN SYNC with SdfMeshProbeDepth.Visibility and SdfOctahedral. The CPU reads the same packed FP16 moments.
float sdfMeshProbeDepthVisibility(uint address, float3 toward) {
    float distance = length(toward);
    if (!(distance > 0.0)) { return 1.0; }
    float2 texel = (sdfOctEncode(toward) * 0.5 + 0.5) * SDF_MESH_IRRADIANCE_DEPTH_SIZE - 0.5;
    int2 base = (int2)floor(texel);
    float2 fraction = texel - (float2)base;
    float mean = 0.0;
    float squared = 0.0;
    [unroll] for (uint corner = 0u; corner < 4u; corner++) {
        int2 high = int2(corner & 1u, corner >> 1);
        float2 axes = lerp(1.0 - fraction, fraction, (float2)high);
        float weight = axes.x * axes.y;
        int2 tap = sdfOctBorderSource(base + high, (int)SDF_MESH_IRRADIANCE_DEPTH_SIZE);
        uint word = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_DEPTH_WORD + (uint)(tap.x + SDF_MESH_IRRADIANCE_DEPTH_SIZE * tap.y)];
        mean += weight * sdfMeshIrradianceHalf(word, 0u);
        squared += weight * sdfMeshIrradianceHalf(word, 1u);
    }
    if (distance <= mean) { return 1.0; }
    float variance = max(0.0, squared - mean * mean);
    float delta = distance - mean;
    float probability = variance / max(1.0e-8, variance + delta * delta);
    float visibility = probability * probability * probability;
    return (visibility < 0.2) ? (visibility * visibility * visibility / 0.04) : visibility;
}
struct SdfMeshProbes {
    uint address[8];
    float weight[8];
};

// Whether a gathered probe answers.
bool sdfMeshProbeAnswers(SdfMeshProbes probes, uint corner) {
    return (probes.address[corner] != SdfMeshProbeNone);
}
SdfMeshProbes sdfMeshProbesAt(float3 position, float3 normal) {
    SdfMeshProbes probes;
    uint4 volume = passGroup.meshIrradianceVolume;
    float spacing = asfloat(volume.y);
    int3 dims = int3((volume.z & 1023u), ((volume.z >> 10) & 1023u), ((volume.z >> 20) & 1023u));
    int3 low = passGroup.meshIrradianceLow.xyz;
    uint count, stride;
    sdfMeshIrradiance.GetDimensions(count, stride);

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.address[corner] = SdfMeshProbeNone;
        probes.weight[corner] = 0.0;
    }
    if ((volume.x == 0u) || !(spacing > 0.0) || any(dims <= 0) || any(!isfinite(position))) {
        return probes;
    }

    float3 pushed = (position + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS));
    // Extend the boundary probes only as far as their visibility permits. The pushed receiver, never the clamped
    // lookup, is tested against their depth moments, so leaving the window cannot invent open sky.
    float3 lattice = clamp(((pushed / spacing) - 0.5), (float3)low, (float3)(low + dims - 1));
    float3 base = floor(lattice);
    float3 fraction = (lattice - base);
    int3 first = int3(base);
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        int3 cell = (first + offset);

        if (any(cell < low) || any(cell >= (low + dims))) {
            continue;
        }

        uint address = (sdfMeshIrradianceSlot(cell, dims) * SDF_MESH_IRRADIANCE_PROBE_WORDS);

        if (((address + SDF_MESH_IRRADIANCE_PROBE_WORDS) > count) || (sdfMeshIrradiance[address] != (sdfMeshIrradianceKey(cell) | SDF_MESH_IRRADIANCE_WRITTEN))) {
            continue;
        }

        float3 axes = lerp((1.0 - fraction), fraction, float3(offset));
        // The trilinear share is the cell's; the direction, the distance and the depth test are the probe's own position.
        float3 probePoint = ((((float3)cell + 0.5) * spacing) + sdfMeshProbeOffset(address));
        float3 toward = (probePoint - position);
        float distance = length(toward);
        float facing = ((distance > 0.0) ? max(0.0, dot((toward / distance), normal)) : 1.0);
        float weight = (((axes.x * axes.y * axes.z) * (facing * facing)) * sdfMeshProbeDepthVisibility(address, (pushed - probePoint)));

        if (weight <= 0.0) {
            continue;
        }
        probes.address[corner] = address;
        probes.weight[corner] = weight;
    }

    return probes;
}
// The sky visibility and reflected radiance the gathered probes hold, seen along `direction` (the normal for the light a
// surface receives, the mirror direction for the light it reflects). A point with no answering probe has no measured
// incoming light. The boundary gather extrapolates only the light its probes' depth moments permit.
void sdfMeshIrradianceAt(SdfMeshProbes probes, float3 direction, out float skyVisibility, out float3 radiance) {
    float sky = 0.0;
    float3 reflected = 0.0;
    float total = 0.0;

    skyVisibility = 0.0;
    radiance = 0.0;
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        if (!sdfMeshProbeAnswers(probes, corner)) {
            continue;
        }

        float probeSky;
        float3 probeRadiance;
        sdfMeshIrradianceProbe(probes.address[corner], direction, probeSky, probeRadiance);
        sky += (probeSky * probes.weight[corner]);
        reflected += (probeRadiance * probes.weight[corner]);
        total += probes.weight[corner];
    }
    if (total <= 0.0) {
        return;
    }

    skyVisibility = (sky / total);
    radiance = (reflected / total);
}
// The visibility of a light of the lights table at the surface: the share of the gathered probes whose centre sees it,
// so a fixture lights nothing through the wall or slab between them; one where no probe takes part.
float sdfMeshLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    uint word = (SDF_MESH_IRRADIANCE_LIGHTS_WORD + (lightIndex >> 5u));
    uint bit = (1u << (lightIndex & 31u));
    float visible = 0.0;
    float total = 0.0;

    if (lightIndex >= (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)) {
        return 1.0;
    }
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        if (!sdfMeshProbeAnswers(probes, corner)) {
            continue;
        }
        if ((sdfMeshIrradiance[probes.address[corner] + word] & bit) != 0u) {
            visible += probes.weight[corner];
        }
        total += probes.weight[corner];
    }
    if (total <= 0.0) {
        return 1.0;
    }

    return (visible / total);
}
// The mesh path's indirect light at a surface point, in the views pass's units (the cosine-weighted mean radiance about
// the normal): the live sky's irradiance over pi under the volume's sky visibility, when the sky source is
// on, and the reflected radiance, whose sources the CPU trace already gated and scaled.
float3 sdfMeshIndirect(SdfMeshProbes probes, float3 normal) {
    float skyVisibility;
    float3 radiance;
    sdfMeshIrradianceAt(probes, normal, skyVisibility, radiance);

    float3 sky = 0.0;
    if ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u) {
        sky = (sdfSkyPhysicalIrradiance(normal) * ((skyVisibility * passGroup.indirectSourceGains.w) / 3.141592653589793));
    }
    return (sky + radiance);
}
// The environment a surface reflects along a mirror direction, occluded by the volume: the lighting-visible sky as far as
// the probes about the point see it open along that direction, and where they do not, the light the surfaces about them
// send back that way. A room reflects its walls, not the sky outside it.
float3 sdfMeshReflection(SdfMeshProbes probes, float3 direction, float3 environment) {
    float skyVisibility;
    float3 radiance;
    sdfMeshIrradianceAt(probes, direction, skyVisibility, radiance);

    return ((environment * skyVisibility) + radiance);
}
#endif
