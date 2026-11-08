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
    return (passGroup.meshIrradianceVolume != 0u);
}
uint sdfMeshIrradianceKey(int3 cell) {
    uint3 low = (asuint(cell) & 1023u);
    return (low.x | (low.y << 10) | (low.z << 20));
}
// The cell's coordinates modulo the volume's size, from 0, wrapped unsigned after a lift by 2^16 sizes, which covers every
// cell within 2^16 volumes of the origin: NVIDIA's Vulkan driver answers a signed remainder (OpSRem) of a negative operand
// wrongly, where Direct3D 12 answers it right.
uint3 sdfMeshIrradianceWrapped(int3 cell, int3 dims) {
    return ((uint3)(cell + (dims * 65536)) % (uint3)dims);
}
uint sdfMeshIrradianceSlot(int3 cell, int3 dims) {
    uint3 size = (uint3)dims;
    uint3 wrapped = sdfMeshIrradianceWrapped(cell, dims);
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
// Unwritten, invalid and invisible probes take no part: their weight is zero.

// Where the probe stands, relative to its cell's centre: the offset the trace moved it by out of geometry or off a
// face it lay on, zero for a probe at its centre. Every position the gather reads is the centre plus this.
// KEEP IN SYNC with SdfMeshIrradiance.Compose's offset words.
float3 sdfMeshProbeOffset(uint address) {
    uint first = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD];
    uint second = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_OFFSET_WORD + 1u];

    return float3(sdfMeshIrradianceHalf(first, 0u), sdfMeshIrradianceHalf(first, 1u), sdfMeshIrradianceHalf(second, 0u));
}
// One minus the smoothstep of a blocked share over a closing ramp from `start`, over one over `scale`.
float sdfMeshProbeRamp(float blocked, float start, float scale) {
    float coverage = saturate((blocked - start) * scale);
    coverage = coverage * coverage * (3.0 - 2.0 * coverage);
    return 1.0 - coverage;
}
// The visibility the taps' blocked shares leave, at every level alike: the taps on solids close the receiver once they are
// the majority, the taps on sheets (`sheets`, part of `blocked`) at almost any share. KEEP IN SYNC with
// SdfMeshProbeCoverage.Visibility.
float sdfMeshProbeCoverageVisibility(float blocked, float sheets) {
    return sdfMeshProbeRamp(blocked, SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_START, SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_SCALE)
        * sdfMeshProbeRamp(sheets, SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_START, SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_SCALE);
}
// First-hit surface support in four reflected angular taps, never whole-sphere plane vetoes.
// KEEP IN SYNC with SdfMeshProbeDepth.Visibility, its signed octahedral normal bytes and its sheet bit (the low bit of a
// texel's curvature half, masked off before the curvature decodes).
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
    float4 sheets = 0.0;
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
        uint curvatureHalf = (((index & 1u) != 0u) ? (packedCurvature >> 16) : (packedCurvature & 0xffffu));
        float curvature = f16tof32(curvatureHalf & 0xfffeu);
        if (!isfinite(curvature)) { continue; }
        sheets[corner] = (((curvatureHalf & 1u) != 0u) ? 1.0 : 0.0);
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
        float closed = saturate(-gap / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
        return sdfMeshProbeCoverageVisibility(planeWeight * closed, dot(weights, sheets) * closed);
    }
    float blocked = 0.0;
    float blockedSheets = 0.0;
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
        float share = weights[corner] * saturate(-gap / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
        blocked += share;
        blockedSheets += share * sheets[corner];
    }
    return sdfMeshProbeCoverageVisibility(blocked, blockedSheets);
}
// A level's probe spacing along each axis, zero for a level not published, and its window: first cell and probes along
// each axis.
float3 sdfMeshIrradianceSpacing(uint level) {
    return passGroup.meshIrradianceSpacings[level].xyz;
}
int3 sdfMeshIrradianceDims(uint level) {
    uint packed = (uint)passGroup.meshIrradianceLevels[level].w;
    return int3((packed & 1023u), ((packed >> 10) & 1023u), ((packed >> 20) & 1023u));
}
// A level's first slot in the volume: its slots follow the finer levels' windows.
uint sdfMeshIrradianceBase(uint level) {
    uint base = 0u;

    [unroll] for (uint finer = 0u; (finer < SDF_MESH_IRRADIANCE_LEVELS); finer++) {
        uint3 size = (uint3)sdfMeshIrradianceDims(finer);

        base += ((finer < level) ? (size.x * size.y * size.z) : 0u);
    }
    return base;
}

// A stage's eight corners at one level, as a cage word: the wrapped window coordinates of its first corner (its lowest
// cell, which lies inside the level's window wherever the stage answers), ten bits an axis as the window's packing bounds
// them (sdfMeshIrradianceDims), and the level in the top two bits. The cage alone names each corner's probe
// (sdfMeshProbeStageOf), so a gather carries two words for its sixteen probes.
uint sdfMeshProbeCageWord(uint level, int3 first, int3 dims) {
    uint3 wrapped = sdfMeshIrradianceWrapped(first, dims);

    return (wrapped.x | (wrapped.y << 10) | (wrapped.z << 20) | (level << 30));
}
// A cage's corners as word addresses in the volume: its first corner's, and the step to the corner one cell along each
// axis, which wraps back by the window's size where the first corner stands on the window's last probe along that axis.
// The steps add in unsigned arithmetic, so a wrapping step is the stride times one less the size, modulo 2^32.
struct SdfMeshProbeStage {
    uint first;
    uint3 step;
};
SdfMeshProbeStage sdfMeshProbeStageOf(uint cage) {
    uint level = (cage >> 30);
    uint3 wrapped = uint3((cage & 1023u), ((cage >> 10) & 1023u), ((cage >> 20) & 1023u));
    uint3 size = (uint3)sdfMeshIrradianceDims(level);
    uint3 stride = uint3(1u, size.x, (size.x * size.y));
    uint3 back = (stride * (1u - size));
    SdfMeshProbeStage stage;

    stage.first = ((sdfMeshIrradianceBase(level) + wrapped.x + (stride.y * wrapped.y) + (stride.z * wrapped.z)) * SDF_MESH_IRRADIANCE_PROBE_WORDS);
    stage.step = (uint3((((wrapped.x + 1u) == size.x) ? back.x : stride.x), (((wrapped.y + 1u) == size.y) ? back.y : stride.y),
        (((wrapped.z + 1u) == size.z) ? back.z : stride.z)) * SDF_MESH_IRRADIANCE_PROBE_WORDS);
    return stage;
}
// The first word of a cage corner's probe; corner bits 0, 1 and 2 step along x, y and z. KEEP IN SYNC with
// SdfMeshIrradiance.SlotOf, which this equals for every corner inside the level's window.
uint sdfMeshProbeStageAddress(SdfMeshProbeStage stage, uint corner) {
    return (stage.first + (((corner & 1u) != 0u) ? stage.step.x : 0u) + (((corner & 2u) != 0u) ? stage.step.y : 0u)
        + (((corner & 4u) != 0u) ? stage.step.z : 0u));
}

// One stage's eight corners about a receiver, as one level's gather fills them: its cage, and each corner's weight, zero
// for a corner that does not answer.
struct SdfMeshProbeCage {
    uint cage;
    float weight[8];
};
// The probes a receiver reads (SdfMeshIrradiance.GatherSlots): corners 0 to 7 the chosen stage's, 8 to 15 the remainder
// stage's, each weight pre-scaled by its stage's share so the weights sum to one, and each stage's cage
// (sdfMeshProbeCageWord). A corner answers exactly where its weight is positive, and only such a corner lies inside its
// stage's window, so sdfMeshProbeAt is the one door to a corner's probe. KEEP IN SYNC with SdfMeshProbeSelection.
struct SdfMeshProbes {
    uint cage;
    uint remainderCage;
    float weight[16];
    // The chosen stage's share of the answer: one where no remainder stage answers, zero where no stage answers.
    float share;
    // Whether each stage read its probes bounded by their measured reach; false where it read them past it, blocked
    // only by the surfaces they measured.
    bool measured;
    bool remainderMeasured;
};

// The two stages' cages a reader of the gathered probes resolves once before its corners (sdfMeshProbeAt).
struct SdfMeshProbeStages {
    SdfMeshProbeStage chosen;
    SdfMeshProbeStage remainder;
};
SdfMeshProbeStages sdfMeshProbeStagesOf(SdfMeshProbes probes) {
    SdfMeshProbeStages stages;

    stages.chosen = sdfMeshProbeStageOf(probes.cage);
    stages.remainder = sdfMeshProbeStageOf(probes.remainderCage);
    return stages;
}
// Whether a gathered corner answers, and its probe's first word where it does.
bool sdfMeshProbeAt(SdfMeshProbes probes, SdfMeshProbeStages stages, uint corner, out uint address) {
    address = ((corner < 8u) ? sdfMeshProbeStageAddress(stages.chosen, corner) : sdfMeshProbeStageAddress(stages.remainder, corner));
    return (probes.weight[corner] > 0.0);
}
// One level's eight probes about the pushed receiver, its lookup clamped to the level's boundary probes: each corner's
// weight is its trilinear share times the facing fade (at least `behind`, so a receiver whose every visible probe lies
// behind its plane still reads them) times the angular surface visibility of the pushed receiver, `bounded` by each
// probe's measured reach or, unbounded, blocked only by the surfaces it measured.
// Returns the total weight, and in `front` the cage's weight before visibility with the facing fade alone (no behind
// floor) over every present probe, invalid ones included: the support a share is measured against. KEEP IN SYNC with
// SdfMeshIrradiance.Gather.
float sdfMeshProbesGather(uint level, float3 spacing, int3 low, int3 dims, float3 position, float3 normal, float3 pushed, bool bounded, float behind, out float front, out SdfMeshProbeCage probes) {
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

    front = 0.0;
    probes.cage = sdfMeshProbeCageWord(level, first, dims);

    SdfMeshProbeStage stage = sdfMeshProbeStageOf(probes.cage);

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.weight[corner] = 0.0;

        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        int3 cell = (first + offset);

        if (any(cell < low) || any(cell >= (low + dims))) {
            continue;
        }

        uint address = sdfMeshProbeStageAddress(stage, corner);

        if ((address + SDF_MESH_IRRADIANCE_PROBE_WORDS) > count) {
            continue;
        }
        uint key = sdfMeshIrradiance[address];
        if ((key & ~SDF_MESH_IRRADIANCE_INVALID) != (sdfMeshIrradianceKey(cell) | SDF_MESH_IRRADIANCE_WRITTEN)) {
            continue;
        }

        float3 axes = lerp((1.0 - fraction), fraction, float3(offset));
        float trilinear = (axes.x * axes.y * axes.z);
        // The trilinear share is the cell's; the direction, the distance and the depth test are the probe's own position.
        float3 probePoint = ((((float3)cell + 0.5) * spacing) + sdfMeshProbeOffset(address));
        float3 toward = (probePoint - position);
        // The fade runs over half the cell's extent along the normal, half the spacing for a cube.
        float fade = smoothstep(0.0, 0.5 * length(spacing * normal), dot(toward, normal));
        front += (trilinear * lerp(fade, 1.0, outside));
        if ((key & SDF_MESH_IRRADIANCE_INVALID) != 0u) {
            continue;
        }
        float weight = (trilinear * lerp(max(fade, behind), 1.0, outside));

        if (weight > 0.0) {
            weight *= sdfMeshProbeDepthVisibility(address, (pushed - probePoint), bounded);
        }
        if (weight <= 0.0) {
            continue;
        }
        probes.weight[corner] = weight;
        total += weight;
    }

    return total;
}
// The share a finer level's stage keeps of its answer past its window, `beyond` metres past its boundary probes: whole up
// to SDF_MESH_IRRADIANCE_SURFACE_REACH_FADE_START of its `reach`, easing to none at the reach, so the next stage takes the
// receiver over across that band. KEEP IN SYNC with SdfMeshIrradiance.ReachShareOf.
float sdfMeshIrradianceReachShare(float beyond, float reach) {
    float start = (SDF_MESH_IRRADIANCE_SURFACE_REACH_FADE_START * reach);
    float eased = saturate((beyond - start) / (reach - start));
    return (1.0 - ((eased * eased) * (3.0 - (2.0 * eased))));
}
// How far past a level's boundary probes the pushed receiver stands, in metres; zero within them.
float sdfMeshIrradianceBeyond(float3 pushed, float3 spacing, int3 low, int3 dims) {
    float3 unclamped = ((pushed / spacing) - 0.5);

    return length((unclamped - clamp(unclamped, (float3)low, (float3)(low + dims - 1))) * spacing);
}
// The probes a receiver reads. KEEP IN SYNC with SdfMeshIrradiance.Select: a chain of stages, finest first: each finer
// published level whose boundary probes' surface maps reach the receiver past its window, then the level whose window
// holds it (the finest such, the coarsest published past every window) bounded by its probes' measured reach, the same
// probes unbounded, blocked only by the surfaces they measured, and last the finest level's boundary probes unbounded.
// A coarser level never answers inside a finer window, where its metres-wide angular support would see through a thin
// wall a finer probe measured. The first stage whose probes answer is chosen, in its share: its weight over the same
// cage's front weight before visibility, clamped to one, so a single corner an 8 m level measured across a slab never
// stands for the whole answer. The rest comes from the next stage over another cage (a later stage of the chosen level
// reads the same probes), normalized to that rest; where none answers, the chosen stage is normalized to the whole. A
// finer level's stage past its window answers in its share times its reach share (sdfMeshIrradianceReachShare).
// Only the finest level keeps the facing floor (SDF_MESH_IRRADIANCE_BEHIND_SHARE): its depth test resolves the
// receiver's own surface, while a coarser probe behind the receiver's plane is as likely beyond the receiver's own wall.
SdfMeshProbes sdfMeshProbesAt(float3 position, float3 normal) {
    SdfMeshProbes probes;

    probes.cage = 0u;
    probes.remainderCage = 0u;
    probes.share = 0.0;
    probes.measured = false;
    probes.remainderMeasured = false;
    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        probes.weight[corner] = 0.0;
    }
    if ((passGroup.meshIrradianceVolume == 0u) || any(!isfinite(position))) {
        return probes;
    }

    float3 pushed = (position + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS));
    uint chosen = SDF_MESH_IRRADIANCE_LEVELS;
    bool held = false;

    // The finest published level holding the receiver, else the coarsest published.
    [unroll] for (uint level = 0u; (level < SDF_MESH_IRRADIANCE_LEVELS); level++) {
        float3 levelSpacing = sdfMeshIrradianceSpacing(level);
        int3 levelDims = sdfMeshIrradianceDims(level);

        if (!held && all(levelSpacing > 0.0) && all(levelDims > 0)) {
            chosen = level;
            held = (sdfMeshIrradianceBeyond(pushed, levelSpacing, passGroup.meshIrradianceLevels[level].xyz, levelDims) <= 0.0);
        }
    }
    if (chosen == SDF_MESH_IRRADIANCE_LEVELS) {
        return probes;
    }

    // One rolled loop, so the gather and its surface test inline once: each finer published level whose probes' reach
    // covers the receiver, the chosen level's measured read, its extended read, then the finest level's extended read,
    // until a stage answers in full or a second stage answers.
    bool found = false;
    uint first = 0u;
    float firstTotal = 0.0;
    float share = 1.0;
    bool remainder = false;

    [loop] for (uint attempt = 0u; (attempt <= (chosen + 2u)); attempt++) {
        uint level = ((attempt > (chosen + 1u)) ? 0u : min(attempt, chosen));
        bool bounded = (attempt <= chosen);
        float3 spacing = sdfMeshIrradianceSpacing(level);
        int3 low = passGroup.meshIrradianceLevels[level].xyz;
        int3 dims = sdfMeshIrradianceDims(level);

        if (((attempt < chosen) || (attempt > (chosen + 1u))) && (!all(spacing > 0.0) || any(dims <= 0) || (chosen == 0u))) {
            continue;
        }
        float reachShare = 1.0;
        if (attempt < chosen) {
            float beyond = sdfMeshIrradianceBeyond(pushed, spacing, low, dims);
            float reach = ((SDF_MESH_IRRADIANCE_SURFACE_REACH * length(spacing)) + SDF_MESH_IRRADIANCE_NORMAL_BIAS);
            if (beyond > reach) {
                continue;
            }
            reachShare = sdfMeshIrradianceReachShare(beyond, reach);
        }
        if (found && (level == first)) {
            continue;
        }

        SdfMeshProbeCage cage;
        float front;
        float total = sdfMeshProbesGather(level, spacing, low, dims, position, normal, pushed, bounded, ((level == 0u) ? SDF_MESH_IRRADIANCE_BEHIND_SHARE : 0.0), front, cage);

        if (!(total > 0.0)) {
            continue;
        }
        if (!found) {
            found = true;
            first = level;
            firstTotal = total;
            probes.measured = bounded;
            probes.cage = cage.cage;
            share = (((front > total) ? (total / front) : 1.0) * reachShare);
            [unroll] for (uint chosenCorner = 0u; (chosenCorner < 8u); chosenCorner++) {
                probes.weight[chosenCorner] = cage.weight[chosenCorner];
            }
            if (share >= 1.0) {
                break;
            }
            continue;
        }
        remainder = true;
        probes.remainderMeasured = bounded;
        probes.remainderCage = cage.cage;
        [unroll] for (uint restCorner = 0u; (restCorner < 8u); restCorner++) {
            probes.weight[8u + restCorner] = (cage.weight[restCorner] * ((1.0 - share) / total));
        }
        break;
    }
    if (!found) {
        return probes;
    }
    share = (remainder ? share : 1.0);
    probes.share = share;
    [unroll] for (uint scaled = 0u; (scaled < 8u); scaled++) {
        probes.weight[scaled] *= (share / firstTotal);
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

    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(probes);

    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        uint address;
        if (!sdfMeshProbeAt(probes, stages, corner, address)) {
            continue;
        }

        float probeSky;
        float3 probeRadiance;
        sdfMeshIrradianceProbe(address, direction, probeSky, probeRadiance);
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
// The visibility of a light of the lights table at the surface (a traced point or spot light, or a shadowing directional
// in a view with no cascade): the weight of the gathered probes read bounded by their
// measured reach whose centre sees it, so a fixture lights nothing through the wall or slab between them and its light
// scales with the measured share of the answer; zero where no measured probe takes part. KEEP IN SYNC with
// SdfMeshIrradiance.LightVisibility.
float sdfMeshLightVisibility(SdfMeshProbes probes, uint lightIndex) {
    uint word = (SDF_MESH_IRRADIANCE_LIGHTS_WORD + (lightIndex >> 5u));
    uint bit = (1u << (lightIndex & 31u));
    float visible = 0.0;

    if (lightIndex >= (32u * SDF_MESH_IRRADIANCE_LIGHT_WORDS)) {
        return 1.0;
    }

    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(probes);

    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        uint address;
        if (!sdfMeshProbeAt(probes, stages, corner, address) || !((corner < 8u) ? probes.measured : probes.remainderMeasured)) {
            continue;
        }
        if ((sdfMeshIrradiance[address + word] & bit) != 0u) {
            visible += probes.weight[corner];
        }
    }

    return visible;
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
