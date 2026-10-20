// The baked-mesh path's indirect light (views only): the irradiance volume SdfMeshIrradiance traces on the CPU against the
// static meshes, read at a shaded point by the eight probes about it. It performs no field query. The probe record and the
// normal bias are SdfMeshIrradianceProbe's (Puck.SdfVm.Model), generated into sdf-isa.hlsli (SDF_MESH_IRRADIANCE_*); the
// key, the toroidal slot and the probe weights are mirrored by SdfMeshIrradiance's KeyOf, SlotOf and Incoming, the CPU
// trace's read of the same volume.
#ifndef SHADE_SDF_MESH_IRRADIANCE_HLSLI
#define SHADE_SDF_MESH_IRRADIANCE_HLSLI
#include "../isa/sdf-isa.hlsli"

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
// The sky visibility and reflected radiance the volume holds about a surface point: the eight probes about the point,
// pushed along its normal, each weighted trilinearly and by how far it lies in front of the surface; a probe of another
// cell, an unwritten one or one inside geometry takes no part. Fades to the open sky and no reflected light across the
// last cell of the volume, and answers that where no probe takes part.
void sdfMeshIrradianceAt(float3 position, float3 normal, out float skyVisibility, out float3 radiance) {
    skyVisibility = 1.0;
    radiance = 0.0;

    uint4 volume = passGroup.meshIrradianceVolume;
    float spacing = asfloat(volume.y);
    int3 dims = int3((volume.z & 1023u), ((volume.z >> 10) & 1023u), ((volume.z >> 20) & 1023u));
    int3 low = passGroup.meshIrradianceLow.xyz;
    uint count, stride;
    sdfMeshIrradiance.GetDimensions(count, stride);

    if ((volume.x == 0u) || !(spacing > 0.0) || any(dims <= 0) || any(!isfinite(position))) {
        return;
    }

    float3 lattice = (((position + (normal * (SDF_MESH_IRRADIANCE_NORMAL_BIAS * spacing))) / spacing) - 0.5);
    float3 base = floor(lattice);
    float3 fraction = (lattice - base);
    int3 first = int3(base);
    float sky = 0.0;
    float3 reflected = 0.0;
    float total = 0.0;

    [loop] for (uint corner = 0u; (corner < 8u); corner++) {
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
        float3 toward = ((((float3)cell + 0.5) * spacing) - position);
        float distance = length(toward);
        float facing = ((distance > 0.0) ? ((dot((toward / distance), normal) + 1.0) * 0.5) : 1.0);
        float weight = ((axes.x * axes.y * axes.z) * ((facing * facing) + 0.2));

        if (weight <= 0.0) {
            continue;
        }

        float probeSky;
        float3 probeRadiance;
        sdfMeshIrradianceProbe(address, normal, probeSky, probeRadiance);
        sky += (probeSky * weight);
        reflected += (probeRadiance * weight);
        total += weight;
    }

    if (total <= 0.0) {
        return;
    }

    float3 inside = min((lattice - (float3)low), ((float3)(low + dims - 1) - lattice));
    float fade = saturate(min(inside.x, min(inside.y, inside.z)));

    skyVisibility = lerp(1.0, (sky / total), fade);
    radiance = ((reflected / total) * fade);
}
// The mesh path's indirect light at a surface point, in the views pass's units (the cosine-weighted mean radiance about
// the normal): the live sky's irradiance over pi under the volume's sky visibility, when the sky source is
// on, and the reflected radiance, whose sources the CPU trace already gated and scaled.
float3 sdfMeshIndirect(float3 position, float3 normal) {
    float skyVisibility;
    float3 radiance;
    sdfMeshIrradianceAt(position, normal, skyVisibility, radiance);

    float3 sky = 0.0;
    if ((passGroup.indirectSources & SDF_INDIRECT_SOURCES_SKY) != 0u) {
        sky = (sdfSkyPhysicalIrradiance(normal) * ((skyVisibility * passGroup.indirectSourceGains.w) / 3.141592653589793));
    }
    return (sky + radiance);
}
#endif
