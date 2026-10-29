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
// probe takes no part) and weight, and whether it lies in front of the surface.
// A corner's weight is its trilinear share times how far it lies in front of the surface times how far the pushed
// point lies on the probe's side of its blocking planes (sdfMeshProbePlaneVisibility), times the share of the other
// answering probes it sees (sdfMeshProbeMutualShares); a probe of another cell, an unwritten one or one inside geometry
// takes no part. The probes in front of the surface answer, and apart those behind it, which answer only where none
// lies in front: a probe behind a wall or a slab sees the room beyond it, which may be lit many times as brightly.
static const uint SdfMeshProbeNone = 0xffffffffu;

// The neighbour bit of the lattice offset `d`, each component in -1, 0 and 1 and not all zero, so the bit of -d is 25
// less it. KEEP IN SYNC with SdfMeshIrradiance.NeighbourIndex.
uint sdfMeshProbeNeighbourIndex(int3 d) {
    uint index = (uint)(((9 * (d.x + 1)) + (3 * (d.y + 1))) + (d.z + 1));

    return ((index > 13u) ? (index - 1u) : index);
}
// The plane the probe's ray toward its neighbour at lattice offset `d` first hit, relative to the probe's centre: a
// normal facing away from the probe and the distance along it; zero where the ray reached open space.
float4 sdfMeshProbePlane(uint address, int3 d) {
    uint word = (address + SDF_MESH_IRRADIANCE_PLANES_WORD + (2u * sdfMeshProbeNeighbourIndex(d)));
    uint first = sdfMeshIrradiance[word];
    uint second = sdfMeshIrradiance[word + 1u];

    return float4(sdfMeshIrradianceHalf(first, 0u), sdfMeshIrradianceHalf(first, 1u), sdfMeshIrradianceHalf(second, 0u), sdfMeshIrradianceHalf(second, 1u));
}
// How far a plane admits a point at `toward` from the probe's centre: one on the probe's side of it, fading to nothing
// over SDF_MESH_IRRADIANCE_DEPTH_FADE metres past it; a zero plane admits everything.
float sdfMeshProbePlaneAdmits(float4 plane, float3 toward) {
    return (1.0 - saturate((dot(plane.xyz, toward) - plane.w) / SDF_MESH_IRRADIANCE_DEPTH_FADE));
}
// How far a probe lets a point at `toward` from its centre take its light: the first-hit planes of its rays in the
// four lattice directions about the point's direction (the 3x3 directions of the cube face the point lies toward,
// blended bilinearly by where the point falls between them), each admitting the point while it lies on the probe's side
// of the plane. A wall or slab the rays stop at is that plane, so a point past it takes nothing whichever way it lies
// from the probe; a finite blocker (a plinth, a stair's nosing) darkens only the directions whose rays meet it, and
// softly between them. `suspect` is the share of the admission the probe's rays did not see for themselves: a ray that
// reached open space, or whose hit plane lies more than a quarter of the spacing beyond the point (fully suspect at
// three quarters), admits the point only for want of a hit. Distance is perpendicular to the plane, so rays meeting
// the point's own surface agree even at oblique or grazing angles. KEEP IN SYNC with SdfMeshIrradiance.PlaneVisibility.
float sdfMeshProbePlaneVisibility(uint address, float3 toward, float spacing, out float suspect) {
    float3 size = abs(toward);
    float major = max(size.x, max(size.y, size.z));

    suspect = 0.0;
    if (!(major > 0.0)) {
        return 1.0;
    }

    // The face is the major component's axis; the point's direction is arranged as (major, minor, minor).
    bool xMajor = ((size.x >= size.y) && (size.x >= size.z));
    bool yMajor = (!xMajor && (size.y >= size.z));
    float3 arranged = (xMajor ? toward.xyz : (yMajor ? toward.yxz : toward.zxy)) / major;
    int facing = ((arranged.x < 0.0) ? -1 : 1);
    // The minor components, from -1 to 1, fall between the face's three texels at -1, 0 and 1.
    float2 minor = (arranged.yz + 1.0);
    float2 cell = min(floor(minor), 1.0);
    float2 fraction = (minor - cell);
    float visibility = 0.0;

    [unroll] for (uint corner = 0u; (corner < 4u); corner++) {
        int2 high = int2((corner & 1u), (corner >> 1));
        float weight = (((high.x != 0) ? fraction.x : (1.0 - fraction.x)) * ((high.y != 0) ? fraction.y : (1.0 - fraction.y)));
        int2 texel = ((int2)cell + high - 1);
        int3 d = (xMajor ? int3(facing, texel.x, texel.y) : (yMajor ? int3(texel.x, facing, texel.y) : int3(texel.x, texel.y, facing)));
        float4 plane = sdfMeshProbePlane(address, d);
        float admits = (weight * sdfMeshProbePlaneAdmits(plane, toward));
        float gap = (any(plane != 0.0) ? (plane.w - dot(plane.xyz, toward)) : 1.0e30);

        visibility += admits;
        suspect += (admits * saturate((gap - (0.25 * spacing)) / (0.5 * spacing)));
    }
    suspect = ((visibility > 0.0) ? (suspect / visibility) : 0.0);
    return visibility;
}
struct SdfMeshProbes {
    uint address[8];
    float weight[8];
    bool front[8];
    // Whether any probe lies in front; when none does, the probes behind answer.
    bool anyFront;
};

// Whether a gathered probe answers: one in front of the surface, or one behind it when none lies in front.
bool sdfMeshProbeAnswers(SdfMeshProbes probes, uint corner) {
    return ((probes.address[corner] != SdfMeshProbeNone) && (probes.front[corner] || !probes.anyFront));
}
// Scales each answering probe's weight by how much of the other answering probes' weight it sees and is seen by across
// the lattice (each probe's neighbour bits), faded in from SDF_MESH_IRRADIANCE_MUTUAL_LOW to _HIGH of it, so a probe that
// stands across a wall from the rest, which a window or doorway in one of its directions admits, gives way to
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

// A probe's 26 rays are one line wide each, so a probe across a roof, floor or wall from the point still admits it
// through the rays that cleared the blocker's edge or passed a doorway in it (its suspect share,
// sdfMeshProbePlaneVisibility). Where another answering probe cannot see it (their neighbour bit is clear), that
// probe's plane toward it is the blocker between them, and a point on the near side of that plane lies across it from
// the far probe, which gives way by its suspect share; a probe whose rays met the point's own surface keeps lighting
// it, so a stair flight between two probes separates neither from the walls beside it. A separator's veto is weighted
// by how much of it survives the same test, read from the weights before any veto, so a fully separated probe vetoes
// nothing and two probes that separate each other leave each other standing; a probe that vetoes another fully is
// therefore unseparated itself, and the gather never empties. The separating plane has no finite extent: it is the
// blocker's whole half-space. Every index is a constant once the loops unroll. KEEP IN SYNC with
// SdfMeshIrradiance.Separate.
void sdfMeshProbeSeparate(inout SdfMeshProbes probes, float suspect[8], int3 first, float spacing, float3 pushed) {
    uint neighbours[8];
    float separated[8];
    float support[64];

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        neighbours[corner] = (sdfMeshProbeAnswers(probes, corner) ? sdfMeshIrradiance[probes.address[corner] + SDF_MESH_IRRADIANCE_NEIGHBOURS_WORD] : 0u);
    }
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        separated[corner] = 0.0;

        [unroll] for (uint other = 0u; (other < 8u); other++) {
            uint pair = ((8u * corner) + other);

            support[pair] = 0.0;
            if (!sdfMeshProbeAnswers(probes, corner) || (suspect[corner] <= 0.0) || (other == corner) || !sdfMeshProbeAnswers(probes, other)) {
                continue;
            }

            int3 otherOffset = int3((other & 1u), ((other >> 1) & 1u), ((other >> 2) & 1u));
            int3 d = (offset - otherOffset);

            if (((neighbours[other] >> sdfMeshProbeNeighbourIndex(d)) & 1u) != 0u) {
                continue;
            }

            float3 otherPoint = (((float3)(first + otherOffset) + 0.5) * spacing);

            support[pair] = sdfMeshProbePlaneAdmits(sdfMeshProbePlane(probes.address[other], d), (pushed - otherPoint));

            separated[corner] = max(separated[corner], (suspect[corner] * support[pair]));
        }
    }
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        if (!sdfMeshProbeAnswers(probes, corner)) {
            continue;
        }

        float veto = 0.0;

        [unroll] for (uint other = 0u; (other < 8u); other++) {
            if ((other == corner) || !sdfMeshProbeAnswers(probes, other)) {
                continue;
            }
            veto = max(veto, ((suspect[corner] * support[(8u * corner) + other]) * (1.0 - separated[other])));
        }
        probes.weight[corner] *= (1.0 - veto);
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
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.address[corner] = SdfMeshProbeNone;
        probes.weight[corner] = 0.0;
        probes.front[corner] = false;
    }
    if ((volume.x == 0u) || !(spacing > 0.0) || any(dims <= 0) || any(!isfinite(position))) {
        return probes;
    }

    float3 pushed = (position + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS));
    // Extend the boundary probes only as far as their visibility permits. The pushed receiver, never the clamped
    // lookup, is tested against their blocking planes, so leaving the window cannot invent open sky.
    float3 lattice = clamp(((pushed / spacing) - 0.5), (float3)low, (float3)(low + dims - 1));
    float3 base = floor(lattice);
    float3 fraction = (lattice - base);
    int3 first = int3(base);
    float suspect[8];
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        int3 cell = (first + offset);

        suspect[corner] = 0.0;
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
        float unseen;
        float weight = (((axes.x * axes.y * axes.z) * ((facing * facing) + 0.2)) * sdfMeshProbePlaneVisibility(address, (pushed - probePoint), spacing, unseen));

        if (weight <= 0.0) {
            continue;
        }

        bool front = (dot(toward, normal) >= (-SDF_MESH_IRRADIANCE_BEHIND * spacing));

        suspect[corner] = unseen;
        probes.address[corner] = address;
        probes.weight[corner] = weight;
        probes.front[corner] = front;
        probes.anyFront = (probes.anyFront || front);
    }

    sdfMeshProbeSeparate(probes, suspect, first, spacing, pushed);
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        if ((probes.weight[corner] <= 0.0) || !sdfMeshProbeAnswers(probes, corner)) {
            probes.address[corner] = SdfMeshProbeNone;
            probes.front[corner] = false;
        }
    }
    sdfMeshProbeMutualShares(probes);

    return probes;
}
// The sky visibility and reflected radiance the gathered probes hold, seen along `direction` (the normal for the light a
// surface receives, the mirror direction for the light it reflects). A point with no answering probe has no measured
// incoming light. The boundary gather extrapolates only the light its probes' blocking planes permit.
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
