// The environment map's coefficients, one group of one invocation a map row: the nine real spherical harmonics of the map
// per colour channel (SdfSkyEnvironment.Project), each texel weighted by its solid angle and the sums scaled by 4π over the
// weights' total. Each invocation sums its row in column order; then each of the first SDF_SKY_REDUCE_SUMS invocations
// adds one sum over the rows in row order, so the coefficients are the same on every run of one device, and every
// groupshared element is written once and read only behind the barrier after its write. The first nine invocations each
// write one coefficient, its three channels and a zero, and count one texel written; the reduction evaluates no sky.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

// Each row's sums: the nine coefficients' three channels, then the weights.
#define SDF_SKY_REDUCE_SUMS 28
groupshared float sdfSkyReduce[SDF_SKY_REDUCE_SUMS][SdfSkyEnvironmentSize];
// Each sum's total over the rows.
groupshared float sdfSkyReduced[SDF_SKY_REDUCE_SUMS];

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_GroupThreadID) {
    uint row = id.x;
    float sums[SDF_SKY_REDUCE_SUMS];

    [unroll] for (uint sum = 0u; sum < (uint)SDF_SKY_REDUCE_SUMS; sum++) {
        sums[sum] = 0.0;
    }
    [loop] for (uint column = 0u; column < (uint)SdfSkyEnvironmentSize; column++) {
        float3 direction = sdfSkyEnvironmentDirection(uint2(column, row));
        float weight = sdfSkyEnvironmentSolidAngle(direction);
        float3 color = sdfSkyEnvironmentUnpack(sdfSkyEnvironment[((row * (uint)SdfSkyEnvironmentSize) + column)]) - sdfSkyEnvironmentUnpack(sdfSkyEnvironment[0]);
        float basis[9];

        sdfSkyEnvironmentBasis(direction, basis);
        [unroll] for (uint k = 0u; k < SdfSkyEnvironmentCoefficients; k++) {
            float3 term = ((weight * basis[k]) * color);

            sums[(k * 3u)] += term.r;
            sums[((k * 3u) + 1u)] += term.g;
            sums[((k * 3u) + 2u)] += term.b;
        }
        sums[(SDF_SKY_REDUCE_SUMS - 1)] += weight;
    }
    [unroll] for (uint stored = 0u; stored < (uint)SDF_SKY_REDUCE_SUMS; stored++) {
        sdfSkyReduce[stored][row] = sums[stored];
    }
    GroupMemoryBarrierWithGroupSync();
    if (row < (uint)SDF_SKY_REDUCE_SUMS) {
        float total = 0.0;

        [loop] for (uint added = 0u; added < (uint)SdfSkyEnvironmentSize; added++) {
            total += sdfSkyReduce[row][added];
        }
        sdfSkyReduced[row] = total;
    }
    GroupMemoryBarrierWithGroupSync();
    if (row < SdfSkyEnvironmentCoefficients) {
        float normalization = (12.566370614359172 / sdfSkyReduced[(SDF_SKY_REDUCE_SUMS - 1)]);

        sdfSkyCoefficientsRW[row] = float4((sdfSkyReduced[(row * 3u)] * normalization), (sdfSkyReduced[((row * 3u) + 1u)] * normalization), (sdfSkyReduced[((row * 3u) + 2u)] * normalization), 0.0);
        if (row == 0u) {
            sdfSkyCoefficientsRW[0] += float4(sdfSkyEnvironmentUnpack(sdfSkyEnvironment[0]) * 3.544907701811032, 0.0);
        }
        sdfWorkTexels = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
