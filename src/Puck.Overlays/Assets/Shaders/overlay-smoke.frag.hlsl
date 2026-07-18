// Puck.Overlays dual-compile smoke test (single-source HLSL; DXC -> SPIR-V + DXIL). Exercises the CompileShaders
// MSBuild target and overlay-common.hlsli's resource-parameter decode trio before any real overlay surface exists,
// so the P0 skeleton proves the whole chain (include resolution, both bytecode targets) instead of deferring the
// first compile to P1. Not wired into a render pass. P1's UnifiedOverlayNode fragment shader supersedes this file;
// delete it then.
[[vk::binding(1, 0)]] StructuredBuffer<uint> smokeData : register(t1);

#include "overlay-common.hlsli"

float4 PSMain(float4 fragCoord : SV_Position) : SV_Target {
    float2 coverage = SampleGlyphCoverage(smokeData, 0u, 0, 95, float2(0.0, 0.0), float2(1.0, 1.0), 1, 1, 1.0, 0.2);

    return float4(coverage.x, coverage.y, 0.0, 1.0);
}
