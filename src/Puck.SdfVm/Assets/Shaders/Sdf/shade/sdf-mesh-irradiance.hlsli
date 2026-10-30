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
// a short distance fade in front of the receiver times angular surface visibility.
// Continuous hemisphere weighting excludes probes behind a surface without a discrete front/back fallback.
// Unwritten, invalid and invisible probes take no part.
static const uint SdfMeshProbeNone = 0xffffffffu;

// Where the probe stands, relative to its cell's centre: the offset the trace moved it by out of geometry or off a
// face it lay on, zero for a probe at its centre. Every position the gather reads is the centre plus this.
// KEEP IN SYNC with SdfMeshIrradiance.Compose's offset words.
float3 sdfMeshProbeOffset(uint address) {
    uint first = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD];
    uint second = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD + 1u];

    return float3(sdfMeshIrradianceHalf(first, 0u), sdfMeshIrradianceHalf(first, 1u), sdfMeshIrradianceHalf(second, 0u));
}
// First-hit surface support in four reflected angular taps, never whole-sphere plane vetoes.
// KEEP IN SYNC with SdfMeshProbeDepth.Visibility and its signed octahedral normal bytes.
float sdfMeshProbeCoverageVisibility(float blocked) {
    float coverage = saturate((blocked - SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_START) * SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_SCALE);
    coverage = coverage * coverage * (3.0 - 2.0 * coverage);
    return 1.0 - coverage;
}
// `bounded` false lets a receiver past a measured miss's reach through, so only measured surfaces block it.
float sdfMeshProbeDepthVisibility(uint address, float3 toward, bool bounded) {
    float distance = length(toward);
    if (!(distance > 0.0)) { return 1.0; }
    float2 texel = (sdfOctEncode(toward) * 0.5 + 0.5) * SDF_MESH_IRRADIANCE_DEPTH_SIZE - 0.5;
    int2 base = (int2)floor(texel);
    float2 fraction = texel - (float2)base;
    float3 points[4];
    float3 normals[4];
    float curvatures[4];
    float4 weights = 0.0;
    uint firstCode = 0xffffffffu;
    float3 firstNormal = 0.0;
    bool planar = true;
    float planeLow = 3.402823466e+38;
    float planeHigh = -3.402823466e+38;
    float planeSum = 0.0;
    float planeWeight = 0.0;
    [unroll] for (uint corner = 0u; corner < 4u; corner++) {
        points[corner] = 0.0;
        normals[corner] = 0.0;
        int2 high = int2(corner & 1u, corner >> 1);
        float2 axes = lerp(1.0 - fraction, fraction, (float2)high);
        float weight = axes.x * axes.y;
        if (weight <= 0.0) { continue; }
        int2 tap = sdfOctBorderSource(base + high, (int)SDF_MESH_IRRADIANCE_DEPTH_SIZE);
        uint word = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_DEPTH_WORD + (uint)(tap.x + SDF_MESH_IRRADIANCE_DEPTH_SIZE * tap.y)];
        float depth = sdfMeshIrradianceHalf(word, 0u);
        if (depth < 0.0) {
            if (bounded && (distance > -depth)) { return 0.0; }
            continue;
        }
        int2 encoded = int2((int)(word << 8u) >> 24, (int)word >> 24);
        points[corner] = depth * sdfOctDecode(((float2)tap + 0.5) * (2.0 / SDF_MESH_IRRADIANCE_DEPTH_SIZE) - 1.0);
        uint index = (uint)(tap.x + SDF_MESH_IRRADIANCE_DEPTH_SIZE * tap.y);
        uint packedCurvature = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_DEPTH_WORD + SDF_MESH_IRRADIANCE_DEPTH_SIZE * SDF_MESH_IRRADIANCE_DEPTH_SIZE + (index >> 1u)];
        float curvature = sdfMeshIrradianceHalf(packedCurvature, index & 1u);
        if (!isfinite(curvature)) { continue; }
        uint code = word >> 16u;
        if (firstCode == 0xffffffffu) {
            firstCode = code;
            firstNormal = sdfOctDecode((float2)encoded / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
        }
        normals[corner] = code == firstCode ? firstNormal : sdfOctDecode((float2)encoded / SDF_MESH_IRRADIANCE_DEPTH_NORMAL_SCALE);
        curvatures[corner] = curvature;
        weights[corner] = weight;
        planar = planar && curvature == 0.0 && code == firstCode;
        float plane = dot(firstNormal, points[corner]);
        planeLow = min(planeLow, plane);
        planeHigh = max(planeHigh, plane);
        planeSum += weight * plane;
        planeWeight += weight;
    }
    if (planar && planeWeight > 0.0 && planeHigh - planeLow <= 0.5 * SDF_MESH_IRRADIANCE_DEPTH_SURFACE_JOIN_GAP) {
        float gap = dot(firstNormal, toward) - planeSum / planeWeight;
        return sdfMeshProbeCoverageVisibility(planeWeight * saturate(-gap / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE));
    }
    float blocked = 0.0;
    [unroll] for (uint corner = 0u; corner < 4u; corner++) {
        if (weights[corner] <= 0.0) { continue; }
        float quadratic = 0.0;
        float3 coefficient = 0.0;
        float constant = 0.0;
        float total = 0.0;
        [unroll] for (uint neighbour = 0u; neighbour < 4u; neighbour++) {
            if (weights[neighbour] <= 0.0) { continue; }
            float3 sum = normals[corner] + normals[neighbour];
            if (dot(normals[corner], normals[neighbour]) <= 0.0 ||
                abs(dot(sum, points[neighbour] - points[corner])) > SDF_MESH_IRRADIANCE_DEPTH_SURFACE_JOIN_GAP) { continue; }
            float curvature = curvatures[neighbour];
            float3 position = points[neighbour];
            float3 normal = normals[neighbour];
            quadratic += weights[neighbour] * 0.5 * curvature;
            coefficient += weights[neighbour] * (normal - curvature * position);
            constant += weights[neighbour] * (-dot(normal, position) + 0.5 * curvature * dot(position, position));
            total += weights[neighbour];
        }
        quadratic /= total;
        coefficient /= total;
        constant /= total;
        float a = quadratic * dot(toward, toward);
        float b = dot(coefficient, toward);
        float c = constant;
        float discriminant = b * b - 4.0 * a * c;
        if (c <= 0.0 || discriminant <= 0.0) { continue; }
        float divisor = sqrt(discriminant) - b;
        if (divisor <= 1e-8) { continue; }
        float along = 2.0 * c / divisor;
        if (along <= 0.0 || along >= 1.0) { continue; }
        float3 hit = toward * along;
        float3 gradient = coefficient + 2.0 * quadratic * hit;
        float gap = dot(normalize(gradient), toward - hit);
        blocked += weights[corner] * saturate(-gap / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
    }
    return sdfMeshProbeCoverageVisibility(blocked);
}
struct SdfMeshProbes {
    uint address[8];
    float weight[8];
    // Whether the probes answered as measured; false where the gather fell back to the nearest level's probes without
    // their surface test.
    bool measured;
};

// Whether a gathered probe answers.
bool sdfMeshProbeAnswers(SdfMeshProbes probes, uint corner) {
    return (probes.address[corner] != SdfMeshProbeNone);
}
// A level's probe spacing, zero for a level not published, and its window: first cell and probes along each axis.
float sdfMeshIrradianceSpacing(uint level) {
    uint4 volume = passGroup.meshIrradianceVolume;
    return asfloat((level == 0u) ? volume.y : ((level == 1u) ? volume.z : volume.w));
}
int3 sdfMeshIrradianceDims(uint level) {
    uint packed = (uint)passGroup.meshIrradianceLevels[level].w;
    return int3((packed & 1023u), ((packed >> 10) & 1023u), ((packed >> 20) & 1023u));
}
// One level's eight probes about the pushed receiver, its lookup clamped to the level's boundary probes: each corner's
// weight is its trilinear share times the facing fade times the angular surface visibility of the pushed receiver,
// `bounded` by each probe's measured reach or, unbounded, blocked only by the surfaces it measured. Returns the total
// weight. KEEP IN SYNC with SdfMeshIrradiance.Gather.
float sdfMeshProbesGather(uint base, float spacing, int3 low, int3 dims, float3 position, float3 normal, float3 pushed, bool bounded, inout SdfMeshProbes probes) {
    uint count, stride;
    sdfMeshIrradiance.GetDimensions(count, stride);

    // The pushed receiver, never the clamped lookup, is tested against the probes' surface samples, so leaving the
    // window cannot invent open sky.
    float3 unclamped = ((pushed / spacing) - 0.5);
    float3 lattice = clamp(unclamped, (float3)low, (float3)(low + dims - 1));
    float3 lower = floor(lattice);
    float3 fraction = (lattice - lower);
    int3 first = int3(lower);
    // How far past the window's boundary probes the receiver stands, in cells, at most one. Beyond the window every
    // boundary probe lies on the window's side of the receiver, where the facing term would light only the normals turned
    // toward the window; it eases to one over that first cell, so a receiver outside takes the boundary probes alike
    // whichever way it faces, as far as their depth admits it.
    float outside = saturate(length(unclamped - lattice));
    float total = 0.0;

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.address[corner] = SdfMeshProbeNone;
        probes.weight[corner] = 0.0;

        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        int3 cell = (first + offset);

        if (any(cell < low) || any(cell >= (low + dims))) {
            continue;
        }

        uint address = ((base + sdfMeshIrradianceSlot(cell, dims)) * SDF_MESH_IRRADIANCE_PROBE_WORDS);

        if (((address + SDF_MESH_IRRADIANCE_PROBE_WORDS) > count) || (sdfMeshIrradiance[address] != (sdfMeshIrradianceKey(cell) | SDF_MESH_IRRADIANCE_WRITTEN))) {
            continue;
        }

        float3 axes = lerp((1.0 - fraction), fraction, float3(offset));
        // The trilinear share is the cell's; the direction, the distance and the depth test are the probe's own position.
        float3 probePoint = ((((float3)cell + 0.5) * spacing) + sdfMeshProbeOffset(address));
        float3 toward = (probePoint - position);
        float facing = lerp(smoothstep(0.0, 0.5 * spacing, dot(toward, normal)), 1.0, outside);
        float weight = ((axes.x * axes.y * axes.z) * facing);

        if (weight > 0.0) {
            weight *= sdfMeshProbeDepthVisibility(address, (pushed - probePoint), bounded);
        }
        if (weight <= 0.0) {
            continue;
        }
        probes.address[corner] = address;
        probes.weight[corner] = weight;
        total += weight;
    }

    return total;
}
// The probes a receiver reads. KEEP IN SYNC with SdfMeshIrradiance.Select: those of the finest published level whose
// window holds it within its boundary probes, the coarsest published level past every window, extended as far as its
// boundary probes' surface support admits. A coarser level never answers inside a finer window, where its metres-wide
// angular support would see through a thin wall a finer probe measured. When none of the level's measured probes
// answer, the same probes answer past their measured reach, blocked only by the surfaces they measured, `measured`
// false: they are the best estimate of an unmeasured receiver's light, where none would draw it black, and a surface
// they saw between them and it still hides it.
SdfMeshProbes sdfMeshProbesAt(float3 position, float3 normal) {
    SdfMeshProbes probes;

    probes.measured = false;
    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.address[corner] = SdfMeshProbeNone;
        probes.weight[corner] = 0.0;
    }
    if ((passGroup.meshIrradianceVolume.x == 0u) || any(!isfinite(position))) {
        return probes;
    }

    float3 pushed = (position + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS));
    uint chosen = SDF_MESH_IRRADIANCE_LEVELS;
    uint chosenBase = 0u;
    uint base = 0u;
    bool held = false;

    // The finest published level holding the receiver, else the coarsest published.
    [unroll] for (uint level = 0u; (level < SDF_MESH_IRRADIANCE_LEVELS); level++) {
        float levelSpacing = sdfMeshIrradianceSpacing(level);
        int3 levelDims = sdfMeshIrradianceDims(level);
        int3 levelLow = passGroup.meshIrradianceLevels[level].xyz;

        if (!held && (levelSpacing > 0.0) && all(levelDims > 0)) {
            float3 unclamped = ((pushed / levelSpacing) - 0.5);

            chosen = level;
            chosenBase = base;
            held = all(unclamped == clamp(unclamped, (float3)levelLow, (float3)(levelLow + levelDims - 1)));
        }
        base += (uint)(levelDims.x * levelDims.y * levelDims.z);
    }
    if (chosen == SDF_MESH_IRRADIANCE_LEVELS) {
        return probes;
    }

    float spacing = sdfMeshIrradianceSpacing(chosen);
    int3 low = passGroup.meshIrradianceLevels[chosen].xyz;
    int3 dims = sdfMeshIrradianceDims(chosen);

    // One rolled loop, so the gather and its surface test inline once: the measured read, then the extended one.
    [loop] for (uint pass = 0u; (pass < 2u); pass++) {
        bool bounded = (pass == 0u);

        if ((sdfMeshProbesGather(chosenBase, spacing, low, dims, position, normal, pushed, bounded, probes) > 0.0) || !bounded) {
            probes.measured = bounded;
            break;
        }
    }

    return probes;
}
// The sky visibility and reflected radiance the gathered probes hold, seen along `direction` (the normal for the light a
// surface receives, the mirror direction for the light it reflects). A point with no answering probe has no measured
// incoming light. The boundary gather extrapolates only the light its probes' surface samples permit.
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
// so a fixture lights nothing through the wall or slab between them; zero where no measured probe takes part.
float sdfMeshLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    uint word = (SDF_MESH_IRRADIANCE_LIGHTS_WORD + (lightIndex >> 5u));
    uint bit = (1u << (lightIndex & 31u));
    float visible = 0.0;
    float total = 0.0;

    if (lightIndex >= (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)) {
        return 1.0;
    }
    if (!probes.measured) {
        return 0.0;
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
        return 0.0;
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
