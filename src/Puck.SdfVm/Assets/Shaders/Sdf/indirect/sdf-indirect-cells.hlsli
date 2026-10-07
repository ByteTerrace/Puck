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

SdfIndirectPlacement sdfIndirectPlace(float3 lattice, float spacing) {
    SdfIndirectPlacement result;
    result.position = lattice;
    result.classification = SdfIndirectClassInactive;
    if (!all(isfinite(lattice)) || !isfinite(spacing) || spacing <= 0.0) { return result; }
    SdfHit sample = sdfIndirectSample(lattice, SDF_INSTANCE_MASK_ALL);
    float clearance = sdfMapBallClearance(sample.distance);
    if (!isfinite(clearance)) { return result; }
    if (clearance >= (sqrt(3.0) + SdfIndirectRelocationAllowance) * spacing) {
        result.classification = SdfIndirectClassDormant;
    } else if (clearance >= SdfIndirectMinimumClearance * spacing) {
        result.classification = SdfIndirectClassActive;
    } else {
        float3 normal = sdfIndirectGradient(lattice);
        float travel = SdfIndirectMinimumClearance * spacing - sample.distance;
        if (dot(normal, normal) == 0.0 || travel > SdfIndirectRelocationAllowance * spacing) { return result; }
        float3 moved = lattice + normal * travel;
        sample = sdfIndirectSample(moved, SDF_INSTANCE_MASK_ALL);
        clearance = sdfMapBallClearance(sample.distance);
        if (clearance >= SdfIndirectMinimumClearance * spacing) {
            result.classification = SdfIndirectClassRelocated;
            result.position = moved;
        }
    }
    return result;
}

SdfIndirectCell sdfIndirectPartition(SdfIndirectPlacement corners[8], float spacing) {
    uint roots[8];
    float3 blocked[56];
    uint blockedPairs[56];
    uint blockedCount = 0u;
    [unroll] for (uint i = 0u; i < 8u; i++) { roots[i] = i; }
    [loop] for (uint first = 0u; first < 7u; first++) {
        [loop] for (uint second = first + 1u; second < 8u; second++) {
            if (corners[first].classification == SdfIndirectClassInactive || corners[second].classification == SdfIndirectClassInactive) { continue; }
            uint budget = SdfIndirectSegmentSteps;
            float3 position;
            bool clear = sdfIndirectSegment(corners[first].position, corners[second].position, budget, position);
            if (!clear) {
                if (all(isfinite(position))) {
                    blocked[blockedCount] = position;
                    blockedPairs[blockedCount++] = first | (second << 3u);
                }
                budget = SdfIndirectSegmentSteps;
                sdfIndirectSegment(corners[second].position, corners[first].position, budget, position);
                if (all(isfinite(position))) {
                    blocked[blockedCount] = position;
                    blockedPairs[blockedCount++] = first | (second << 3u);
                }
            }
            if (clear) {
                uint a = roots[first];
                uint b = roots[second];
                [unroll] for (uint k = 0u; k < 8u; k++) { if (roots[k] == b) { roots[k] = a; } }
            }
        }
    }
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

bool sdfIndirectLaunch(float3 surface, float3 normal, float spacing, inout uint budget, out float3 position, out float clearance) {
    uint start = sdfIndirectEvaluations;
    position = surface;
    clearance = 0.0;
    if (!all(isfinite(surface)) || !all(isfinite(normal)) || !isfinite(spacing) || spacing <= 0.0) { return false; }
    float height = SdfIndirectSurfaceEpsilon;
    float target = max(height, spacing * SdfIndirectReceiverBias);
    bool descended = false;
    // Descent and ascent share one field call site; each call otherwise inlines the complete interpreter.
    // Resetting height at the phase boundary retains both original sample sequences and their shared allowance.
    [loop] for (uint step = 0u; step < max(SdfIndirectLaunchSteps, SdfIndirectNearSteps) && budget > 0u; step++) {
        budget--;
        float3 candidate = surface + normal * height;
        SdfHit sample = sdfIndirectSample(candidate, SDF_INSTANCE_MASK_ALL);
        float ball = sdfMapBallClearance(sample.distance);
        if (!(ball > 0.0) || !isfinite(ball)) { break; }
        if (!descended) {
            height -= ball;
            if (height <= SdfIndirectResolution) { descended = true; height = SdfIndirectSurfaceEpsilon; }
            continue;
        }
        position = candidate;
        clearance = ball;
        float delta = target - height;
        if (ball >= delta) { position = surface + normal * target; clearance = ball - delta; break; }
        height += ball;
    }
    sdfIndirectLaunchEvaluations += sdfIndirectEvaluations - start;
    return descended && clearance > 0.0;
}
#endif
