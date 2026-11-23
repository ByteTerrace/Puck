// The baked-mesh path's indirect light: the irradiance volume SdfMeshIrradiance traces against the static meshes, read at
// a shaded point (by the views and glass passes) and at a probe's hit (by the relight's bounce and the exposure meter)
// through the probes about it. It performs no field query. The probe record and the normal bias are
// SdfMeshIrradianceProbe's (Puck.SdfVm.Model), generated into sdf-render.hlsli (SDF_MESH_IRRADIANCE_*); the key, the
// toroidal slot and the probe weights are mirrored by SdfMeshIrradiance's Holds, SlotOf and Incoming, the CPU read of the
// same volume.
#ifndef SHADE_SDF_MESH_IRRADIANCE_HLSLI
#define SHADE_SDF_MESH_IRRADIANCE_HLSLI
#include "../isa/sdf-render.hlsli"
#include "../field/sdf-octahedral.hlsli"
#include "../field/sdf-component.hlsli"

bool sdfMeshIrradianceHolds(uint address, uint flags, int3 cell) {
    uint at = address + SDF_MESH_IRRADIANCE_CELL_WORD;
    return (flags & SDF_MESH_IRRADIANCE_WRITTEN) != 0u &&
        all(uint3(sdfMeshIrradiance[at], sdfMeshIrradiance[at + 1], sdfMeshIrradiance[at + 2]) == asuint(cell));
}
// Floor modulo with unsigned remainder, including at INT_MIN.
uint3 sdfMeshIrradianceWrapped(int3 cell, int3 dims) {
    uint3 remainder = select(cell < 0, asuint(~cell), asuint(cell)) % (uint3)dims;
    return select(cell < 0, (uint3)dims - 1u - remainder, remainder);
}
uint sdfMeshIrradianceSlot(int3 cell, int3 dims) {
    uint3 size = (uint3)dims;
    uint3 wrapped = sdfMeshIrradianceWrapped(cell, dims);
    return (wrapped.x + (size.x * (wrapped.y + (size.y * wrapped.z))));
}
float sdfMeshIrradianceHalf(uint word, uint high) {
    return f16tof32((high != 0u) ? (word >> 16) : (word & 0xffffu));
}
// The ambient cube's value about a normal: each axis's face toward the normal (+X, -X, +Y, -Y, +Z, -Z), weighted by the
// normal's squared component. The face one axis reads; every six-axis cube a view reads (a probe's, the camera sample's
// in sdfMeshCameraLight) chooses its faces here.
uint sdfMeshCubeFace(float3 normal, uint axis) {
    return ((2u * axis) + ((normal[axis] < 0.0) ? 1u : 0u));
}
// A six-axis cube about a normal, read from its words in `words`: the sky shares from `skyWord` (three words, an axis's
// positive face in the low half and its negative face in the high), and the radiance and the sky light from `radianceWord`
// and `skyLightWord` (nine words each, face f's channel c the half (3f + c) counting from the low half of the first word),
// each word `stride` words past the one before it. A probe's slot holds its cube so with a stride of one
// (SDF_MESH_IRRADIANCE_*_WORD), and a views probe record its cell's blended cube a plane apart (sdf-views-probes.hlsli).
void sdfMeshCubeRead(StructuredBuffer<uint> words, uint skyWord, uint radianceWord, uint skyLightWord, uint stride, float3 normal,
    out float sky, out float3 radiance, out float3 skyLight) {
    float3 squared = (normal * normal);
    sky = 0.0;
    radiance = 0.0;
    skyLight = 0.0;
    [unroll] for (uint axis = 0u; (axis < 3u); axis++) {
        uint face = sdfMeshCubeFace(normal, axis);
        uint negative = (face & 1u);
        sky += (squared[axis] * sdfMeshIrradianceHalf(words[skyWord + (axis * stride)], negative));
        float3 value;
        float3 open;
        [unroll] for (uint channel = 0u; (channel < 3u); channel++) {
            uint entry = ((3u * face) + channel);
            value[channel] = sdfMeshIrradianceHalf(words[radianceWord + ((entry >> 1) * stride)], (entry & 1u));
            open[channel] = sdfMeshIrradianceHalf(words[skyLightWord + ((entry >> 1) * stride)], (entry & 1u));
        }
        radiance += (squared[axis] * value);
        skyLight += (squared[axis] * open);
    }
}
// A probe's sky share, its surfaces' radiance and its sky light, all read from its slot's words.
void sdfMeshIrradianceProbe(uint address, float3 normal, out float sky, out float3 radiance, out float3 skyLight) {
    sdfMeshCubeRead(sdfMeshIrradiance, (address + SDF_MESH_IRRADIANCE_SKY_WORD), (address + SDF_MESH_IRRADIANCE_RADIANCE_WORD),
        (address + SDF_MESH_IRRADIANCE_SKY_LIGHT_WORD), 1u, normal, sky, radiance, skyLight);
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
// The visibility the taps' blocked shares leave: at a coarser level the taps on solids close the receiver once they are
// the majority, at the finest smoothly over their whole share, and the taps on sheets (`sheets`, part of `blocked`, marked
// by the coarser levels alone) at almost any share. KEEP IN SYNC with SdfMeshProbeCoverage.Visibility.
float sdfMeshProbeCoverageVisibility(float blocked, float sheets, bool finest) {
    float start = (finest ? SDF_MESH_IRRADIANCE_DEPTH_FINEST_COVERAGE_START : SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_START);
    float scale = (finest ? SDF_MESH_IRRADIANCE_DEPTH_FINEST_COVERAGE_SCALE : SDF_MESH_IRRADIANCE_DEPTH_COVERAGE_SCALE);

    return sdfMeshProbeRamp(blocked, start, scale)
        * sdfMeshProbeRamp(sheets, SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_START, SDF_MESH_IRRADIANCE_DEPTH_SHEET_COVERAGE_SCALE);
}
// First-hit surface support in four reflected angular taps, never whole-sphere plane vetoes.
// KEEP IN SYNC with SdfMeshProbeDepth.Visibility, its signed octahedral normal bytes and its sheet bit (the low bit of a
// texel's curvature half, masked off before the curvature decodes).
// `bounded` false lets a receiver past a measured miss's reach through, so only measured surfaces block it; `finest`
// names a probe of the finest level, which closes by that level's rule.
float sdfMeshProbeDepthVisibility(uint address, float3 toward, bool bounded, bool finest) {
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
    // Every answering tap's bilinear weight, and where its ray stands at its hit, or at the receiver's distance if it runs
    // that far (SdfMeshProbeDepth.Unresolved reads both).
    float4 taps = 0.0;
    float3 reaches[4];
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
        reaches[corner] = 0.0;
        int2 high = int2(corner & 1u, corner >> 1);
        float2 axes = lerp(1.0 - fraction, fraction, (float2)high);
        float weight = axes.x * axes.y;
        if (weight <= 0.0) { continue; }
        int2 tap = sdfOctBorderSource(base + high, (int)SDF_MESH_IRRADIANCE_DEPTH_SIZE);
        uint word = sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_DEPTH_WORD + (uint)(tap.x + SDF_MESH_IRRADIANCE_DEPTH_SIZE * tap.y)];
        float depth = sdfMeshIrradianceHalf(word, 0u);
        float3 direction = sdfOctDecode(((float2)tap + 0.5) * (2.0 / SDF_MESH_IRRADIANCE_DEPTH_SIZE) - 1.0);
        taps[corner] = weight;
        reaches[corner] = direction * min(abs(depth), distance);
        if (depth < 0.0) {
            if (bounded && (distance > -depth)) { return 0.0; }
            continue;
        }
        int2 encoded = int2((int)(word << 8u) >> 24, (int)word >> 24);
        points[corner] = depth * direction;
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
    float blocked = 0.0;
    float blockedSheets = 0.0;
    float4 closures = 0.0;
    float3 crossings[4];
    float3 sides[4];
    // The exact planar form of the quadratic: one plane every hit tap lies on.
    bool flat = planar && planeWeight > 0.0 && planeHigh - planeLow <= 0.5 * SDF_MESH_IRRADIANCE_DEPTH_SURFACE_JOIN_GAP;
    float offset = (planeWeight > 0.0) ? (planeSum / planeWeight) : 0.0;
    float closed = saturate((offset - dot(firstNormal, toward)) / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
    [unroll] for (uint corner = 0u; corner < 4u; corner++) {
        crossings[corner] = firstNormal * offset;
        sides[corner] = firstNormal;
        if (flat) {
            closures[corner] = (weights[corner] > 0.0) ? closed : 0.0;
            blocked += weights[corner] * closed;
            blockedSheets += weights[corner] * closed * sheets[corner];
            continue;
        }
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
        float3 side = normalize(coefficient + 2.0 * quadratic * hit);
        float gap = dot(side, toward - hit);
        float closure = saturate(-gap / SDF_MESH_IRRADIANCE_DEPTH_SURFACE_FADE);
        float share = weights[corner] * closure;
        closures[corner] = closure;
        crossings[corner] = hit;
        sides[corner] = side;
        blocked += share;
        blockedSheets += share * sheets[corner];
    }
    // At the finest level, the unblocked part of each tap whose ray never got past a blocking tap's surface before the
    // receiver's depth (it stands on the probe's side of that surface, or on its plane within the join gap) carries no
    // evidence about the receiver, in the measure that blocker closes it; the coverage is the blocked share of the taps
    // that do. KEEP IN SYNC with SdfMeshProbeDepth.Unresolved.
    float unresolved = 0.0;
    [unroll] for (uint tap = 0u; tap < 4u; tap++) {
        float before = 0.0;
        [unroll] for (uint blocker = 0u; blocker < 4u; blocker++) {
            if (closures[blocker] > 0.0 && dot(sides[blocker], reaches[tap] - crossings[blocker]) > -SDF_MESH_IRRADIANCE_DEPTH_SURFACE_JOIN_GAP) {
                before = max(before, closures[blocker]);
            }
        }
        unresolved += max(taps[tap] - weights[tap] * closures[tap], 0.0) * before;
    }
    float evidence = finest ? (1.0 - unresolved) : 1.0;
    return sdfMeshProbeCoverageVisibility(blocked / evidence, blockedSheets / evidence, finest);
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
    // One more than the first word of a views probe record whose blended cube (sdfMeshProbeCube) these probes are, read in
    // place of their corners where a reader holds the record; zero for none.
    uint cube;
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
// A gathered corner's weight at a runtime index, chosen by comparison (field/sdf-component.hlsli): indexing the weights
// with a loop counter would copy all sixteen into local memory on every gather that reads them.
float sdfMeshProbeWeight(SdfMeshProbes probes, uint corner) {
    float4 quarter = ((corner < 8u)
        ? ((corner < 4u) ? float4(probes.weight[0], probes.weight[1], probes.weight[2], probes.weight[3])
            : float4(probes.weight[4], probes.weight[5], probes.weight[6], probes.weight[7]))
        : ((corner < 12u) ? float4(probes.weight[8], probes.weight[9], probes.weight[10], probes.weight[11])
            : float4(probes.weight[12], probes.weight[13], probes.weight[14], probes.weight[15])));

    return sdfComponent(quarter, (corner & 3u));
}
// Whether a gathered corner answers, and its probe's first word where it does.
bool sdfMeshProbeAt(SdfMeshProbes probes, SdfMeshProbeStages stages, uint corner, out uint address) {
    address = ((corner < 8u) ? sdfMeshProbeStageAddress(stages.chosen, corner) : sdfMeshProbeStageAddress(stages.remainder, corner));
    return (sdfMeshProbeWeight(probes, corner) > 0.0);
}
// One level's eight probes about the pushed receiver, its lookup clamped to the level's boundary probes: each corner's
// weight is its trilinear share times the facing fade (at least `behind`, so a receiver whose every visible probe lies
// behind its plane still reads them) times the angular surface visibility of the pushed receiver, `bounded` by each
// probe's measured reach or, unbounded, blocked only by the surfaces it measured.
// Returns the total weight, and in `front` the cage's weight before visibility with the facing fade alone (no behind
// floor) over every present probe, invalid ones included: the support a share is measured against. KEEP IN SYNC with
// SdfMeshIrradiance.Gather.
float sdfMeshProbesGather(uint level, float3 spacing, int3 low, int3 dims, float3 position, float3 normal, float3 pushed, bool bounded, float behind, out float front, out float missing, out SdfMeshProbeCage probes) {
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
    missing = 0.0;
    probes.cage = sdfMeshProbeCageWord(level, first, dims);

    SdfMeshProbeStage stage = sdfMeshProbeStageOf(probes.cage);

    [unroll] for (uint corner = 0u; (corner < 8u); corner++) {
        probes.weight[corner] = 0.0;

        int3 offset = int3((corner & 1u), ((corner >> 1) & 1u), ((corner >> 2) & 1u));
        int3 cell = (first + offset);

        if (any(cell < low) || any(cell >= (low + dims))) {
            continue;
        }

        float3 axes = lerp((1.0 - fraction), fraction, float3(offset));
        float trilinear = (axes.x * axes.y * axes.z);
        uint address = sdfMeshProbeStageAddress(stage, corner);

        if ((address + SDF_MESH_IRRADIANCE_PROBE_WORDS) > count) {
            continue;
        }
        uint key = sdfMeshIrradiance[address];
        if (!sdfMeshIrradianceHolds(address, key, cell)) {
            missing += trilinear;
            continue;
        }

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
            weight *= sdfMeshProbeDepthVisibility(address, (pushed - probePoint), bounded, (level == 0u));
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
// The gather of a receiver no probe answers: every weight and the share zero.
SdfMeshProbes sdfMeshProbesNone() {
    SdfMeshProbes probes;

    probes.cage = 0u;
    probes.remainderCage = 0u;
    probes.share = 0.0;
    probes.measured = false;
    probes.remainderMeasured = false;
    probes.cube = 0u;
    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        probes.weight[corner] = 0.0;
    }
    return probes;
}
// The level whose window a pushed receiver's gather chooses: the finest published level holding it, else the coarsest
// published; SDF_MESH_IRRADIANCE_LEVELS where none is published.
uint sdfMeshIrradianceChosen(float3 pushed) {
    uint chosen = SDF_MESH_IRRADIANCE_LEVELS;
    bool held = false;

    [unroll] for (uint level = 0u; (level < SDF_MESH_IRRADIANCE_LEVELS); level++) {
        float3 levelSpacing = sdfMeshIrradianceSpacing(level);
        int3 levelDims = sdfMeshIrradianceDims(level);

        if (!held && all(levelSpacing > 0.0) && all(levelDims > 0)) {
            chosen = level;
            held = (sdfMeshIrradianceBeyond(pushed, levelSpacing, passGroup.meshIrradianceLevels[level].xyz, levelDims) <= 0.0);
        }
    }
    return chosen;
}
// The metres past a level's window its boundary probes' surface maps reach a receiver (the gather's finer stages).
float sdfMeshIrradianceReach(float3 spacing) {
    return ((SDF_MESH_IRRADIANCE_SURFACE_REACH * length(spacing)) + SDF_MESH_IRRADIANCE_NORMAL_BIAS);
}
// The gather, and in `complete` whether every probe it consulted was present: no stage it read found a corner inside its
// window whose slot holds no probe of that corner's cell. Only a complete gather is a function of the windows, the
// published geometry and the receiver alone, since a probe landing in a missing corner changes it without either moving.
// `windowBound` says whether it read a stage whose cage another window's place decides (a finer level's boundary probes
// within their reach, or the finest level's extended read), so a move of that window can change it.
SdfMeshProbes sdfMeshProbesAtComplete(float3 position, float3 normal, out bool complete, out bool windowBound) {
    SdfMeshProbes probes = sdfMeshProbesNone();

    complete = false;
    windowBound = false;
    if (any(!isfinite(position))) {
        return probes;
    }
    complete = true;

    float3 pushed = (position + (normal * SDF_MESH_IRRADIANCE_NORMAL_BIAS));
    uint chosen = sdfMeshIrradianceChosen(pushed);

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

    bool cold = false;
    [loop] for (uint attempt = 0u; attempt <= SDF_MESH_IRRADIANCE_LEVELS + 1u; attempt++) {
        bool bounded = attempt < SDF_MESH_IRRADIANCE_LEVELS;
        if (bounded && attempt > chosen && !cold) { continue; }
        uint level = bounded ? attempt : (attempt == SDF_MESH_IRRADIANCE_LEVELS ? chosen : 0u);
        float3 spacing = sdfMeshIrradianceSpacing(level);
        int3 low = passGroup.meshIrradianceLevels[level].xyz;
        int3 dims = sdfMeshIrradianceDims(level);

        if (!all(spacing > 0.0) || any(dims <= 0) || (attempt > SDF_MESH_IRRADIANCE_LEVELS && chosen == 0u)) {
            continue;
        }
        float reachShare = 1.0;
        if (attempt < chosen) {
            float beyond = sdfMeshIrradianceBeyond(pushed, spacing, low, dims);
            float reach = sdfMeshIrradianceReach(spacing);
            if (beyond > reach) {
                continue;
            }
            reachShare = sdfMeshIrradianceReachShare(beyond, reach);
        }
        if (found && (level == first)) {
            continue;
        }
        windowBound = (windowBound || (attempt < chosen) || (attempt > SDF_MESH_IRRADIANCE_LEVELS));

        SdfMeshProbeCage cage;
        float front, missing;
        float total = sdfMeshProbesGather(level, spacing, low, dims, position, normal, pushed, bounded, ((level == 0u) ? SDF_MESH_IRRADIANCE_BEHIND_SHARE : 0.0), front, missing, cage);
        if (bounded && level >= chosen) { cold = missing > 0; }
        complete = (complete && !(missing > 0.0));
        front += missing;

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
SdfMeshProbes sdfMeshProbesAt(float3 position, float3 normal) {
    bool complete;
    bool windowBound;

    return sdfMeshProbesAtComplete(position, normal, complete, windowBound);
}
// The sky visibility, reflected radiance and sky light the gathered probes hold, seen along `direction` (the normal for
// the light a surface receives, the mirror direction for the light it reflects). A point with no answering probe has no
// measured incoming light. The boundary gather extrapolates only the light its probes' surface samples permit.
void sdfMeshIrradianceAt(SdfMeshProbes probes, float3 direction, out float skyVisibility, out float3 radiance, out float3 skyLight) {
    float sky = 0.0;
    float3 reflected = 0.0;
    float3 open = 0.0;
    float total = 0.0;

    skyVisibility = 0.0;
    radiance = 0.0;
    skyLight = 0.0;

    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(probes);

    [unroll] for (uint corner = 0u; (corner < 16u); corner++) {
        uint address;
        if (!sdfMeshProbeAt(probes, stages, corner, address)) {
            continue;
        }

        float probeSky;
        float3 probeRadiance;
        float3 probeSkyLight;
        sdfMeshIrradianceProbe(address, direction, probeSky, probeRadiance, probeSkyLight);
        sky += (probeSky * probes.weight[corner]);
        reflected += (probeRadiance * probes.weight[corner]);
        open += (probeSkyLight * probes.weight[corner]);
        total += probes.weight[corner];
    }
    if (total <= 0.0) {
        return;
    }

    skyVisibility = (sky / total);
    radiance = (reflected / total);
    skyLight = (open / total);
}
// The gathered probes' six-axis cubes blended into one by their weights, face by face (+X, -X, +Y, -Y, +Z, -Z), each over
// the total weight, zero where no corner answers. A cube's value about a direction is linear in its faces, so this cube
// read along any direction (sdfMeshCubeRead) is sdfMeshIrradianceAt's answer along it, up to the order of the sums.
void sdfMeshProbeCube(SdfMeshProbes probes, out float sky[6], out float3 radiance[6], out float3 skyLight[6]) {
    float total = 0.0;

    [unroll] for (uint cleared = 0u; (cleared < 6u); cleared++) {
        sky[cleared] = 0.0;
        radiance[cleared] = 0.0;
        skyLight[cleared] = 0.0;
    }

    SdfMeshProbeStages stages = sdfMeshProbeStagesOf(probes);

    [loop] for (uint corner = 0u; (corner < 16u); corner++) {
        uint address;
        if (!sdfMeshProbeAt(probes, stages, corner, address)) {
            continue;
        }

        float weight = sdfMeshProbeWeight(probes, corner);

        [unroll] for (uint face = 0u; (face < 6u); face++) {
            sky[face] += (weight * sdfMeshIrradianceHalf(sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_SKY_WORD + (face >> 1)], (face & 1u)));
            [unroll] for (uint channel = 0u; (channel < 3u); channel++) {
                uint entry = ((3u * face) + channel);
                radiance[face][channel] += (weight * sdfMeshIrradianceHalf(sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_RADIANCE_WORD + (entry >> 1)], (entry & 1u)));
                skyLight[face][channel] += (weight * sdfMeshIrradianceHalf(sdfMeshIrradiance[address + SDF_MESH_IRRADIANCE_SKY_LIGHT_WORD + (entry >> 1)], (entry & 1u)));
            }
        }
        total += weight;
    }

    float scale = ((total > 0.0) ? (1.0 / total) : 0.0);

    [unroll] for (uint scaled = 0u; (scaled < 6u); scaled++) {
        sky[scaled] *= scale;
        radiance[scaled] *= scale;
        skyLight[scaled] *= scale;
    }
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
// the normal): the sky light the probes' open rays carry and the surfaces' reflected radiance, whose sources and gains
// the relight already applied.
float3 sdfMeshIndirect(SdfMeshProbes probes, float3 normal) {
    float skyVisibility;
    float3 radiance;
    float3 skyLight;
    sdfMeshIrradianceAt(probes, normal, skyVisibility, radiance, skyLight);

    return (skyLight + radiance);
}
// The environment a surface reflects along a mirror direction, occluded by the volume: the lighting-visible sky as far as
// the probes about the point see it open along that direction, and where they do not, the light the surfaces about them
// send back that way. A room reflects its walls, not the sky outside it.
float3 sdfMeshReflection(SdfMeshProbes probes, float3 direction, float3 environment) {
    float skyVisibility;
    float3 radiance;
    float3 skyLight;
    sdfMeshIrradianceAt(probes, direction, skyVisibility, radiance, skyLight);

    return ((environment * skyVisibility) + radiance);
}
#endif
