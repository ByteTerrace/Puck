// The environment map's coefficients, one group of one invocation a map row: the nine real spherical harmonics of the map
// per colour channel (SdfSkyEnvironment.Project), each texel weighted by its solid angle and the sums scaled by 4π over the
// weights' total. Each invocation sums its row in column order, then the group adds the rows pairwise in a fixed tree,
// so the coefficients are the same on every run of one device. The first nine invocations each write one coefficient,
// its three channels and a zero, and count one texel written; the reduction evaluates no sky.
#include "../isa/sdf-sky-environment.interface.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../shade/sdf-sky-environment.hlsli"

// Each row's sums: the nine coefficients' three channels, then the weights.
#define SDF_SKY_REDUCE_SUMS 28
groupshared float sdfSkyReduce[SDF_SKY_REDUCE_SUMS][SdfSkyEnvironmentSize];

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
        float3 color = sdfSkyEnvironmentUnpack(sdfSkyEnvironmentRW[((row * (uint)SdfSkyEnvironmentSize) + column)]);
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
    [unroll] for (uint stride = ((uint)SdfSkyEnvironmentSize / 2u); stride > 0u; stride >>= 1u) {
        if (row < stride) {
            [unroll] for (uint added = 0u; added < (uint)SDF_SKY_REDUCE_SUMS; added++) {
                sdfSkyReduce[added][row] += sdfSkyReduce[added][(row + stride)];
            }
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (row < SdfSkyEnvironmentCoefficients) {
        float normalization = (12.566370614359172 / sdfSkyReduce[(SDF_SKY_REDUCE_SUMS - 1)][0]);

        sdfSkyCoefficientsRW[row] = float4((sdfSkyReduce[(row * 3u)][0] * normalization), (sdfSkyReduce[((row * 3u) + 1u)][0] * normalization), (sdfSkyReduce[((row * 3u) + 2u)][0] * normalization), 0.0);
        sdfWorkTexels = 1u;
    }

    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
