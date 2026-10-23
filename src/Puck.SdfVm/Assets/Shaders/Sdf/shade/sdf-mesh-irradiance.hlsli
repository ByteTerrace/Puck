// The baked-mesh path's indirect light (views only): the irradiance volume SdfMeshIrradiance traces on the CPU against the
// static meshes, read at a shaded point by the eight probes about it. It performs no field query. The probe record and the
// normal bias are SdfMeshIrradianceProbe's (Puck.SdfVm.Model), generated into sdf-render.hlsli (SDF_MESH_IRRADIANCE_*); the
// key, the toroidal slot and the probe weights are mirrored by SdfMeshIrradiance's KeyOf, SlotOf and Incoming, the CPU
// trace's read of the same volume.
#ifndef SHADE_SDF_MESH_IRRADIANCE_HLSLI
#define SHADE_SDF_MESH_IRRADIANCE_HLSLI
#include "../isa/sdf-render.hlsli"

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

// The eight probes about a surface point, gathered once a pixel: each corner's slot address (SdfMeshProbeNone where the
// probe takes no part) and weight, whether it lies in front of the surface, and the fade across the volume's last cell.
// A corner's weight is its trilinear share times how far it lies in front of the surface times whether the pushed
// point lies within the probe's depth along each axis (sdfMeshProbeDepthVisibility), times the share of the other
// answering probes it sees (sdfMeshProbeMutualShares); a probe of another cell, an unwritten one or one inside geometry
// takes no part. The probes in front of the surface answer, and apart those behind it, which answer only where none
// lies in front: a probe behind a wall or a slab sees the room beyond it, which may be lit many times as brightly.
static const uint SdfMeshProbeNone = 0xffffffffu;

// How far a probe lets a point at `toward` from its centre take its light: one while the point lies nearer along every
// axis than the probe's depth that way, each axis fading to nothing over SDF_MESH_IRRADIANCE_DEPTH_FADE metres past its
// depth, where the wall or slab the probe's rays stop at along that axis stands between them. KEEP IN SYNC with
// SdfMeshIrradiance.DepthVisibility.
float sdfMeshProbeDepthVisibility(uint address, float3 toward) {
    float visibility = 1.0;

    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        float along = toward[axis];
        float depth = sdfMeshIrradianceHalf(sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_DEPTH_WORD + axis], ((along < 0.0) ? 1u : 0u));

        visibility *= (1.0 - saturate((abs(along) - depth) / SDF_MESH_IRRADIANCE_DEPTH_FADE));
    }

    return visibility;
}

struct SdfMeshProbes {
    uint address[8];
    float weight[8];
    bool front[8];
    // Whether any probe lies in front; when none does, the probes behind answer.
    bool anyFront;
    // Whether any traced probe stands about the point, answering or not: when every one stands across a wall or slab
    // from it, the point is enclosed and takes no indirect light, rather than the open sky no traced probe means.
    bool held;
    float fade;
};

// Whether a gathered probe answers: one in front of the surface, or one behind it when none lies in front.
bool sdfMeshProbeAnswers(SdfMeshProbes probes, uint corner) {
    return ((probes.address[corner] != SdfMeshProbeNone) && (probes.front[corner] || !probes.anyFront));
}
// The neighbour bit of the lattice offset `d`, each component in -1, 0 and 1 and not all zero, so the bit of -d is 25
// less it. KEEP IN SYNC with SdfMeshIrradiance.NeighbourIndex.
uint sdfMeshProbeNeighbourIndex(int3 d) {
    uint index = (uint)(((9 * (d.x + 1)) + (3 * (d.y + 1))) + (d.z + 1));

    return ((index > 13u) ? (index - 1u) : index);
}
// Scales each answering probe's weight by how much of the other answering probes' weight it sees and is seen by across
// the lattice (each probe's neighbour bits), faded in from SDF_MESH_IRRADIANCE_MUTUAL_LOW to _HIGH of it, so a probe that
// stands across a wall from the rest, which a window or a doorway on one of its axes let its depth pass, gives way to
// them; where that would leave no weight at all (no answering probe sees another) the weights stand. Every index is a
// constant once the loops unroll. KEEP IN SYNC with SdfMeshIrradiance.MutualShares.
void sdfMeshProbeMutualShares(inout SdfMeshProbes probes) {
    uint neighbours[8];
    float shares[8];
    float kept = 0.0;

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        neighbours[corner] = (sdfMeshProbeAnswers(probes, corner) ? sdfMeshIrradiance[probes.address[corner] + SDF_MESH_IRRADIANCE_NEIGHBOURS_WORD] : 0u);
    }
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        shares[corner] = 1.0;

        float others = 0.0;
        float visible = 0.0;

        [unroll] for (uint other = 0u; (other < 8u); other++) {
            if ((other == corner) || !sdfMeshProbeAnswers(probes, other)) {
                continue;
            }
            others += probes.weight[other];

            int3 d = (int3((other & 1u), ((other >> 1) & 1u), ((other >> 2) & 1u)) - int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u)));
            uint index = sdfMeshProbeNeighbourIndex(d);

            if ((((neighbours[corner] >> index) & 1u) != 0u) && (((neighbours[other] >> (25u - index)) & 1u) != 0u)) {
                visible += probes.weight[other];
            }
        }
        if (sdfMeshProbeAnswers(probes, corner)) {
            if (others > 0.0) {
                shares[corner] = smoothstep(SDF_MESH_IRRADIANCE_MUTUAL_LOW, SDF_MESH_IRRADIANCE_MUTUAL_HIGH, (visible / others));
            }
            kept += (probes.weight[corner] * shares[corner]);
        }
    }
    if (kept > 0.0) {
        [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
            probes.weight[corner] *= shares[corner];
        }
    }
}

SdfMeshProbes sdfMeshProbesAt(float3 position, float3 normal) {
    SdfMeshProbes probes;
    uint4 volume = passGroup.meshIrradianceVolume;
    float spacing = asfloat(volume.y);
    int3 dims = int3((volume.z & 1023u), ((volume.z >> 10) & 1023u), ((volume.z >> 20) & 1023u));
    int3 low = passGroup.meshIrradianceLow.xyz;
    uint count, stride;
    sdfMeshIrradiance.GetDimensions(count, stride);

    probes.anyFront = false;
    probes.held = false;
    probes.fade = 0.0;
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.address[corner] = SdfMeshProbeNone;
        probes.weight[corner] = 0.0;
        probes.front[corner] = false;
    }
    if ((volume.x == 0u) || !(spacing > 0.0) || any(dims <= 0) || any(!isfinite(position))) {
        return probes;
    }

    float3 pushed = (position + (normal * (SDF_MESH_IRRADIANCE_NORMAL_BIAS * spacing)));
    float3 lattice = ((pushed / spacing) - 0.5);
    float3 base = floor(lattice);
    float3 fraction = (lattice - base);
    int3 first = int3(base);
    float3 inside = min((lattice - (float3)low), ((float3)(low + dims - 1) - lattice));

    probes.fade = saturate(min(inside.x, min(inside.y, inside.z)));
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
        float3 probePoint = (((float3)cell + 0.5) * spacing);
        float3 toward = (probePoint - position);
        float distance = length(toward);
        float facing = ((distance > 0.0) ? ((dot((toward / distance), normal) + 1.0) * 0.5) : 1.0);
        float weight = (((axes.x * axes.y * axes.z) * ((facing * facing) + 0.2)) * sdfMeshProbeDepthVisibility(address, (pushed - probePoint)));

        probes.held = true;
        if (weight <= 0.0) {
            continue;
        }

        bool front = (dot(toward, normal) >= (-SDF_MESH_IRRADIANCE_BEHIND * spacing));

        probes.address[corner] = address;
        probes.weight[corner] = weight;
        probes.front[corner] = front;
        probes.anyFront = (probes.anyFront || front);
    }

    sdfMeshProbeMutualShares(probes);

    return probes;
}
// The sky visibility and reflected radiance the gathered probes hold, seen along `direction` (the normal for the light a
// surface receives, the mirror direction for the light it reflects). Fades to the open sky and no reflected light across
// the last cell of the volume, and answers that where no probe is traced; where traced probes stand about the point but
// none answers, every one across a wall or slab from it, the point is enclosed and sees no sky.
void sdfMeshIrradianceAt(SdfMeshProbes probes, float3 direction, out float skyVisibility, out float3 radiance) {
    float sky = 0.0;
    float3 reflected = 0.0;
    float total = 0.0;

    skyVisibility = 1.0;
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
        skyVisibility = (probes.held ? (1.0 - probes.fade) : 1.0);
        return;
    }

    skyVisibility = lerp(1.0, (sky / total), probes.fade);
    radiance = ((reflected / total) * probes.fade);
}
// The visibility of a light of the lights table at the surface: the share of the gathered probes whose centre sees it,
// so a fixture lights nothing through the wall or slab between them; one where no probe takes part, and fading to one
// across the volume's last cell.
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

    return lerp(1.0, (visible / total), probes.fade);
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
