#ifndef SDF_INDIRECT_CELLS_HLSLI
#define SDF_INDIRECT_CELLS_HLSLI
#include "sdf-indirect-march.hlsli"

struct SdfIndirectPlacement { float3 position; uint classification; };
struct SdfIndirectCell { uint components; float3 normal; float offset; };

// IrradianceCells.SmallestEigenvector's covariance fit. Scaling by spacing keeps the matrix in cell units.
float3 sdfIndirectPlaneNormal(float3 points[56], uint count, float3 center, float spacing) {
    float3x3 covariance = (float3x3)0;
    [loop] for (uint index = 0u; index < 56u && index < count; index++) {
        float3 delta = (points[index] - center) / spacing;
        [unroll] for (uint row = 0u; row < 3u; row++) {
            [unroll] for (uint column = 0u; column < 3u; column++) {
                covariance[row][column] += delta[row] * delta[column];
            }
        }
    }
    float3x3 vectors = float3x3(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);
    [loop] for (uint sweep = 0u; sweep < 32u; sweep++) {
        [unroll] for (uint p = 0u; p < 2u; p++) {
            [loop] for (uint q = p + 1u; q < 3u; q++) {
                float offDiagonal = covariance[p][q];
                if (abs(offDiagonal) < 1.0e-12) { continue; }
                float theta = (covariance[q][q] - covariance[p][p]) / (2.0 * offDiagonal);
                float tangent = theta == 0.0 ? 1.0 : sign(theta) / (abs(theta) + sqrt(theta * theta + 1.0));
                float cosine = rsqrt(tangent * tangent + 1.0);
                float sine = tangent * cosine;
                [unroll] for (uint k = 0u; k < 3u; k++) {
                    float kp = covariance[k][p], kq = covariance[k][q];
                    covariance[k][p] = cosine * kp - sine * kq;
                    covariance[k][q] = sine * kp + cosine * kq;
                }
                [unroll] for (uint k = 0u; k < 3u; k++) {
                    float pk = covariance[p][k], qk = covariance[q][k];
                    covariance[p][k] = cosine * pk - sine * qk;
                    covariance[q][k] = sine * pk + cosine * qk;
                }
                [unroll] for (uint k = 0u; k < 3u; k++) {
                    float kp = vectors[k][p], kq = vectors[k][q];
                    vectors[k][p] = cosine * kp - sine * kq;
                    vectors[k][q] = sine * kp + cosine * kq;
                }
            }
        }
    }
    uint least = 0u;
    [unroll] for (uint axis = 1u; axis < 3u; axis++) {
        if (covariance[axis][axis] < covariance[least][least]) { least = axis; }
    }
    return normalize(float3(vectors[0][least], vectors[1][least], vectors[2][least]));
}

// A probe's placement at its lattice point: dormant past every cell it serves, active with the minimum clearance, or
// relocated along the field gradient by at most the relocation allowance. A procedure (sdfIndirectPlaceStep): the
// sample, the gradient and the relocated sample are three queries its owner's run answers.
struct SdfIndirectPlaceProc {
    float3 lattice;
    float spacing;
    SdfIndirectPlacement result;
    SdfHit sample;
    float3 moved;
    uint phase;
};
static SdfIndirectPlaceProc sdfIndirectPlaceProc = (SdfIndirectPlaceProc)0;

uint sdfIndirectPlaceBegin(float3 lattice, float spacing) {
    sdfIndirectPlaceProc.lattice = lattice;
    sdfIndirectPlaceProc.spacing = spacing;
    sdfIndirectPlaceProc.phase = 0u;
    return SdfIndirectProcPlace;
}

uint sdfIndirectPlaceStep() {
    float spacing = sdfIndirectPlaceProc.spacing;
    if (sdfIndirectPlaceProc.phase == 0u) {
        sdfIndirectPlaceProc.result.position = sdfIndirectPlaceProc.lattice;
        sdfIndirectPlaceProc.result.classification = SdfIndirectClassInactive;
        if (!all(isfinite(sdfIndirectPlaceProc.lattice)) || !isfinite(spacing) || spacing <= 0.0) { return SdfIndirectStepReturn; }
        sdfIndirectPlaceProc.phase = 1u;
        return sdfIndirectAsk(sdfIndirectPlaceProc.lattice, SDF_INSTANCE_MASK_ALL);
    }
    if (sdfIndirectPlaceProc.phase == 1u) {
        sdfIndirectPlaceProc.sample = sdfIndirectReply;
        float clearance = sdfMapBallClearance(sdfIndirectPlaceProc.sample.distance);
        if (!isfinite(clearance)) { return SdfIndirectStepReturn; }
        if (clearance >= (sqrt(3.0) + SdfIndirectRelocationAllowance) * spacing) {
            sdfIndirectPlaceProc.result.classification = SdfIndirectClassDormant;
            return SdfIndirectStepReturn;
        }
        if (clearance >= SdfIndirectMinimumClearance * spacing) {
            sdfIndirectPlaceProc.result.classification = SdfIndirectClassActive;
            return SdfIndirectStepReturn;
        }
        sdfIndirectPlaceProc.phase = 2u;
        return sdfIndirectAskGradient(sdfIndirectPlaceProc.lattice);
    }
    if (sdfIndirectPlaceProc.phase == 2u) {
        float3 normal = sdfIndirectReplyGradient;
        float travel = SdfIndirectMinimumClearance * spacing - sdfIndirectPlaceProc.sample.distance;
        if (dot(normal, normal) == 0.0 || travel > SdfIndirectRelocationAllowance * spacing) { return SdfIndirectStepReturn; }
        sdfIndirectPlaceProc.moved = sdfIndirectPlaceProc.lattice + normal * travel;
        sdfIndirectPlaceProc.phase = 3u;
        return sdfIndirectAsk(sdfIndirectPlaceProc.moved, SDF_INSTANCE_MASK_ALL);
    }
    float clearance = sdfMapBallClearance(sdfIndirectReply.distance);
    if (clearance >= SdfIndirectMinimumClearance * spacing) {
        sdfIndirectPlaceProc.result.classification = SdfIndirectClassRelocated;
        sdfIndirectPlaceProc.result.position = sdfIndirectPlaceProc.moved;
    }
    return SdfIndirectStepReturn;
}

// The cell's connected components over its eight placed corners: two corners join when the finite segment between
// them is clear. A blocked pair's both directions record their blocked points, from which a two-component cell fits
// the plane that orders its proof attempts. A procedure (sdfIndirectPartitionStep): each pair's segments are calls its
// owner's run answers.
struct SdfIndirectPartitionProc {
    SdfIndirectPlacement corners[8];
    float spacing;
    uint roots[8];
    float3 blocked[56];
    uint blockedPairs[56];
    uint blockedCount;
    uint first;
    uint second;
    bool clear;
    uint phase;
    SdfIndirectCell result;
};
static SdfIndirectPartitionProc sdfIndirectPartitionProc = (SdfIndirectPartitionProc)0;

uint sdfIndirectPartitionBegin(SdfIndirectPlacement corners[8], float spacing) {
    sdfIndirectPartitionProc.corners = corners;
    sdfIndirectPartitionProc.spacing = spacing;
    sdfIndirectPartitionProc.phase = 0u;
    return SdfIndirectProcPartition;
}

// The components and plane of a partition whose pairs are all decided. No field query.
SdfIndirectCell sdfIndirectPartitionCell(SdfIndirectPlacement corners[8], uint roots[8], float3 blocked[56], uint blockedPairs[56], uint blockedCount, float spacing) {
    SdfIndirectCell cell = (SdfIndirectCell)0;
    uint count = 0u;
    uint labels[8];
    [unroll] for (uint j = 0u; j < 8u; j++) {
        uint label = 15u;
        if (corners[j].classification != SdfIndirectClassInactive) {
            label = count;
            [unroll] for (uint k = 0u; k < 8u && k < j; k++) { if (roots[k] == roots[j]) { label = labels[k]; break; } }
            if (label == count) { count++; }
        }
        labels[j] = label;
        cell.components |= label << (j * 4u);
    }
    // The plane only orders proof attempts. A rejected fit leaves the partition fully usable.
    if (count == 2u && blockedCount >= 3u) {
        uint separated = 0u;
        [loop] for (uint n = 0u; n < 56u && n < blockedCount; n++) {
            uint pair = blockedPairs[n];
            if (labels[pair & 7u] != labels[pair >> 3u]) { blocked[separated++] = blocked[n]; }
        }
        if (separated < 3u) { return cell; }
        float3 center = 0.0;
        [loop] for (uint n = 0u; n < 56u && n < separated; n++) { center += blocked[n]; }
        center /= separated;
        float3 normal = sdfIndirectPlaneNormal(blocked, separated, center, spacing);
        float offset = dot(normal, center);
        bool fits = all(isfinite(normal));
        [loop] for (uint p = 0u; p < 56u && p < separated; p++) { fits = fits && abs(dot(normal, blocked[p]) - offset) <= spacing * SdfIndirectPlaneTolerance; }
        int sideZero = 0, sideOne = 0;
        [unroll] for (uint c = 0u; c < 8u; c++) {
            float side = dot(normal, corners[c].position) - offset;
            if (labels[c] == 0u) { sideZero += side > 0.0 ? 1 : -1; }
            if (labels[c] == 1u) { sideOne += side > 0.0 ? 1 : -1; }
        }
        if (sideOne < 0 && sideZero > 0) { normal = -normal; offset = -offset; }
        else if (!(sideOne > 0 && sideZero < 0)) { fits = false; }
        if (fits) { cell.normal = normal; cell.offset = offset; }
    }
    return cell;
}

uint sdfIndirectPartitionStep() {
    bool advance = false;
    if (sdfIndirectPartitionProc.phase == 0u) {
        sdfIndirectPartitionProc.blockedCount = 0u;
        [unroll] for (uint i = 0u; i < 8u; i++) { sdfIndirectPartitionProc.roots[i] = i; }
        sdfIndirectPartitionProc.first = 0u;
        sdfIndirectPartitionProc.second = 1u;
    } else if (sdfIndirectPartitionProc.phase == 1u) {
        // The forward segment: a clear pair joins, and a blocked one records its point and tries the reverse.
        sdfIndirectPartitionProc.clear = sdfIndirectSegmentProc.clear;
        if (!sdfIndirectPartitionProc.clear) {
            if (all(isfinite(sdfIndirectSegmentProc.blockedPoint))) {
                sdfIndirectPartitionProc.blocked[sdfIndirectPartitionProc.blockedCount] = sdfIndirectSegmentProc.blockedPoint;
                sdfIndirectPartitionProc.blockedPairs[sdfIndirectPartitionProc.blockedCount++] = sdfIndirectPartitionProc.first | (sdfIndirectPartitionProc.second << 3u);
            }
            uint first = sdfIndirectPartitionProc.first;
            uint second = sdfIndirectPartitionProc.second;
            sdfIndirectPartitionProc.phase = 2u;
            return sdfIndirectCall(sdfIndirectSegmentBegin(sdfIndirectPartitionProc.corners[second].position, sdfIndirectPartitionProc.corners[first].position, SdfIndirectSegmentSteps));
        }
        uint a = sdfIndirectPartitionProc.roots[sdfIndirectPartitionProc.first];
        uint b = sdfIndirectPartitionProc.roots[sdfIndirectPartitionProc.second];
        [unroll] for (uint k = 0u; k < 8u; k++) { if (sdfIndirectPartitionProc.roots[k] == b) { sdfIndirectPartitionProc.roots[k] = a; } }
        advance = true;
    } else {
        if (all(isfinite(sdfIndirectSegmentProc.blockedPoint))) {
            sdfIndirectPartitionProc.blocked[sdfIndirectPartitionProc.blockedCount] = sdfIndirectSegmentProc.blockedPoint;
            sdfIndirectPartitionProc.blockedPairs[sdfIndirectPartitionProc.blockedCount++] = sdfIndirectPartitionProc.first | (sdfIndirectPartitionProc.second << 3u);
        }
        advance = true;
    }
    [loop]
    for (;;) {
        if (advance) {
            advance = false;
            sdfIndirectPartitionProc.second++;
            if (sdfIndirectPartitionProc.second >= 8u) {
                sdfIndirectPartitionProc.first++;
                sdfIndirectPartitionProc.second = sdfIndirectPartitionProc.first + 1u;
            }
        }
        if (sdfIndirectPartitionProc.first >= 7u) { break; }
        uint first = sdfIndirectPartitionProc.first;
        uint second = sdfIndirectPartitionProc.second;
        if (sdfIndirectPartitionProc.corners[first].classification == SdfIndirectClassInactive || sdfIndirectPartitionProc.corners[second].classification == SdfIndirectClassInactive) {
            advance = true;
            continue;
        }
        sdfIndirectPartitionProc.phase = 1u;
        return sdfIndirectCall(sdfIndirectSegmentBegin(sdfIndirectPartitionProc.corners[first].position, sdfIndirectPartitionProc.corners[second].position, SdfIndirectSegmentSteps));
    }
    sdfIndirectPartitionProc.result = sdfIndirectPartitionCell(sdfIndirectPartitionProc.corners, sdfIndirectPartitionProc.roots,
        sdfIndirectPartitionProc.blocked, sdfIndirectPartitionProc.blockedPairs, sdfIndirectPartitionProc.blockedCount, sdfIndirectPartitionProc.spacing);
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_PLACE
SdfIndirectPlacement sdfIndirectPlace(float3 lattice, float spacing) {
    sdfIndirectRun(sdfIndirectPlaceBegin(lattice, spacing));
    return sdfIndirectPlaceProc.result;
}
#endif
#ifdef SDF_INDIRECT_PROC_PARTITION
SdfIndirectCell sdfIndirectPartition(SdfIndirectPlacement corners[8], float spacing) {
    sdfIndirectRun(sdfIndirectPartitionBegin(corners, spacing));
    return sdfIndirectPartitionProc.result;
}
#endif
// The launch from a surface: a descent onto the surface, then an ascent to its receiver bias above it, both under the
// whole-ray allowance. Descent and ascent share one sample point; resetting the height at the phase boundary retains
// both original sample sequences and their shared allowance. A procedure (sdfIndirectLaunchStep): its owner's run
// answers each sample.
struct SdfIndirectLaunchProc {
    float3 surface;
    float3 normal;
    float spacing;
    uint budget;
    float3 position;
    float clearance;
    bool launched;
    uint start;
    float height;
    float target;
    bool descended;
    uint step;
    float3 candidate;
    bool resuming;
};
static SdfIndirectLaunchProc sdfIndirectLaunchProc = (SdfIndirectLaunchProc)0;

uint sdfIndirectLaunchBegin(float3 surface, float3 normal, float spacing, uint budget) {
    sdfIndirectLaunchProc.surface = surface;
    sdfIndirectLaunchProc.normal = normal;
    sdfIndirectLaunchProc.spacing = spacing;
    sdfIndirectLaunchProc.budget = budget;
    sdfIndirectLaunchProc.resuming = false;
    return SdfIndirectProcLaunch;
}

uint sdfIndirectLaunchStep() {
    if (!sdfIndirectLaunchProc.resuming) {
        sdfIndirectLaunchProc.start = sdfIndirectEvaluations;
        sdfIndirectLaunchProc.position = sdfIndirectLaunchProc.surface;
        sdfIndirectLaunchProc.clearance = 0.0;
        sdfIndirectLaunchProc.launched = false;
        if (!all(isfinite(sdfIndirectLaunchProc.surface)) || !all(isfinite(sdfIndirectLaunchProc.normal)) || !isfinite(sdfIndirectLaunchProc.spacing) || sdfIndirectLaunchProc.spacing <= 0.0) { return SdfIndirectStepReturn; }
        sdfIndirectLaunchProc.height = SdfIndirectSurfaceEpsilon;
        sdfIndirectLaunchProc.target = max(sdfIndirectLaunchProc.height, sdfIndirectLaunchProc.spacing * SdfIndirectReceiverBias);
        sdfIndirectLaunchProc.descended = false;
        sdfIndirectLaunchProc.step = 0u;
        sdfIndirectLaunchProc.resuming = true;
    } else {
        bool done = false;
        float ball = sdfMapBallClearance(sdfIndirectReply.distance);
        if (!(ball > 0.0) || !isfinite(ball)) {
            done = true;
        } else if (!sdfIndirectLaunchProc.descended) {
            sdfIndirectLaunchProc.height -= ball;
            if (sdfIndirectLaunchProc.height <= SdfIndirectResolution) { sdfIndirectLaunchProc.descended = true; sdfIndirectLaunchProc.height = SdfIndirectSurfaceEpsilon; }
        } else {
            sdfIndirectLaunchProc.position = sdfIndirectLaunchProc.candidate;
            sdfIndirectLaunchProc.clearance = ball;
            float delta = sdfIndirectLaunchProc.target - sdfIndirectLaunchProc.height;
            if (ball >= delta) {
                sdfIndirectLaunchProc.position = sdfIndirectLaunchProc.surface + sdfIndirectLaunchProc.normal * sdfIndirectLaunchProc.target;
                sdfIndirectLaunchProc.clearance = ball - delta;
                done = true;
            } else {
                sdfIndirectLaunchProc.height += ball;
            }
        }
        if (done) {
            sdfIndirectLaunchEvaluations += sdfIndirectEvaluations - sdfIndirectLaunchProc.start;
            sdfIndirectLaunchProc.launched = sdfIndirectLaunchProc.descended && sdfIndirectLaunchProc.clearance > 0.0;
            return SdfIndirectStepReturn;
        }
        sdfIndirectLaunchProc.step++;
    }
    if (sdfIndirectLaunchProc.step < max(SdfIndirectLaunchSteps, SdfIndirectNearSteps) && sdfIndirectLaunchProc.budget > 0u) {
        sdfIndirectLaunchProc.budget--;
        sdfIndirectLaunchProc.candidate = sdfIndirectLaunchProc.surface + sdfIndirectLaunchProc.normal * sdfIndirectLaunchProc.height;
        return sdfIndirectAsk(sdfIndirectLaunchProc.candidate, SDF_INSTANCE_MASK_ALL);
    }
    sdfIndirectLaunchEvaluations += sdfIndirectEvaluations - sdfIndirectLaunchProc.start;
    sdfIndirectLaunchProc.launched = sdfIndirectLaunchProc.descended && sdfIndirectLaunchProc.clearance > 0.0;
    return SdfIndirectStepReturn;
}

#ifdef SDF_INDIRECT_PROC_LAUNCH
bool sdfIndirectLaunch(float3 surface, float3 normal, float spacing, inout uint budget, out float3 position, out float clearance) {
    sdfIndirectRun(sdfIndirectLaunchBegin(surface, normal, spacing, budget));
    budget = sdfIndirectLaunchProc.budget;
    position = sdfIndirectLaunchProc.position;
    clearance = sdfIndirectLaunchProc.clearance;
    return sdfIndirectLaunchProc.launched;
}
#endif
#endif
