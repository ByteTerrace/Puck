// Generated from shader interface 'sdf-mesh' (sha256/dc356244d1da9f924d6b9b7fe283553ae0758fdc2f594b4e9a147194824523dd). Regenerate it from the interface; never edit it.
#ifndef PUCK_SHADER_INTERFACE_SDF_MESH
#define PUCK_SHADER_INTERFACE_SDF_MESH

// The Pass group: descriptor set 3, register space 3.
struct SdfMeshPass {
    [[vk::offset(0)]] uint2 extent;
    [[vk::offset(8)]] float aspectRatio;
    [[vk::offset(12)]] uint cameraTileShadowMask;
    [[vk::offset(16)]] float curvatureCavity;
    [[vk::offset(20)]] float curvatureInk;
    [[vk::offset(24)]] uint _pad24;
    [[vk::offset(28)]] uint _pad28;
    [[vk::offset(32)]] float3 curvatureInkColor;
    [[vk::offset(44)]] float curvatureInkHigh;
    [[vk::offset(48)]] float curvatureInkLow;
    [[vk::offset(52)]] float curvatureRim;
    [[vk::offset(56)]] uint debugMode;
    [[vk::offset(60)]] float debugSliceAxis;
    [[vk::offset(64)]] float debugSliceOffset;
    [[vk::offset(68)]] uint disableAmbientOcclusion;
    [[vk::offset(72)]] uint disableFarBound;
    [[vk::offset(76)]] uint disableScreenLights;
    [[vk::offset(80)]] uint disableShadowCull;
    [[vk::offset(84)]] uint disableSoftShadows;
    [[vk::offset(88)]] uint enableShadowProxy;
    [[vk::offset(92)]] float farDistance;
    [[vk::offset(96)]] uint fastAmbientOcclusion;
    [[vk::offset(100)]] uint fastSoftShadowMarch;
    [[vk::offset(104)]] uint finiteDifferenceNormals;
    [[vk::offset(108)]] uint _pad108;
    [[vk::offset(112)]] float2 frustumOffset;
    [[vk::offset(120)]] uint geometryOnly;
    [[vk::offset(124)]] uint gridFlags;
    [[vk::offset(128)]] float gridLineWidth;
    [[vk::offset(132)]] uint _pad132;
    [[vk::offset(136)]] uint _pad136;
    [[vk::offset(140)]] uint _pad140;
    [[vk::offset(144)]] float4 gridObjectFrame;
    [[vk::offset(160)]] float3 gridObjectOrigin;
    [[vk::offset(172)]] float gridObjectPatchRadius;
    [[vk::offset(176)]] float3 gridObjectPitch;
    [[vk::offset(188)]] float gridPlaneY;
    [[vk::offset(192)]] float4 gridWorldFrame;
    [[vk::offset(208)]] float3 gridWorldOrigin;
    [[vk::offset(220)]] uint _pad220;
    [[vk::offset(224)]] float3 gridWorldPitch;
    [[vk::offset(236)]] uint historyFrames;
    [[vk::offset(240)]] uint2 imageExtent;
    [[vk::offset(248)]] uint2 indirectAllocation;
    [[vk::offset(256)]] float4 indirectApply;
    [[vk::offset(272)]] uint indirectBodies;
    [[vk::offset(276)]] uint indirectCertificateRevision;
    [[vk::offset(280)]] float indirectContact;
    [[vk::offset(284)]] uint indirectEpoch;
    [[vk::offset(288)]] float indirectFeedbackGain;
    [[vk::offset(292)]] uint indirectFrame;
    [[vk::offset(296)]] uint indirectMethod;
    [[vk::offset(300)]] uint indirectNearEnabled;
    [[vk::offset(304)]] uint4 indirectPickPixel;
    [[vk::offset(320)]] uint indirectPreviousPublication;
    [[vk::offset(324)]] uint indirectReadGeneration;
    [[vk::offset(328)]] uint indirectReadPublication;
    [[vk::offset(332)]] uint indirectReceiverProofs;
    [[vk::offset(336)]] float4 indirectSourceGains;
    [[vk::offset(352)]] uint indirectSources;
    [[vk::offset(356)]] uint indirectTier;
    [[vk::offset(360)]] uint instanceMaskWordCount;
    [[vk::offset(364)]] uint _pad364;
    [[vk::offset(368)]] float2 jitter;
    [[vk::offset(376)]] uint lightCount;
    [[vk::offset(380)]] uint lightMapCount;
    [[vk::offset(384)]] float4 lightMaps[84];
    [[vk::offset(1728)]] uint lightSlice;
    [[vk::offset(1732)]] float lightSweepRadius;
    [[vk::offset(1736)]] uint meshBakeCount;
    [[vk::offset(1740)]] uint _pad1740;
    [[vk::offset(1744)]] float4 meshBakeLattices[48];
    [[vk::offset(2512)]] uint meshDraws;
    [[vk::offset(2516)]] uint _pad2516;
    [[vk::offset(2520)]] uint _pad2520;
    [[vk::offset(2524)]] uint _pad2524;
    [[vk::offset(2528)]] float4 meshShadowCascades[12];
    [[vk::offset(2720)]] uint meshShadowCount;
    [[vk::offset(2724)]] uint _pad2724;
    [[vk::offset(2728)]] uint _pad2728;
    [[vk::offset(2732)]] uint _pad2732;
    [[vk::offset(2736)]] float4 meshShadowParameters;
    [[vk::offset(2752)]] float nearDistance;
    [[vk::offset(2756)]] uint preserveIndirectReceivers;
    [[vk::offset(2760)]] uint _pad2760;
    [[vk::offset(2764)]] uint _pad2764;
    [[vk::offset(2768)]] float4 previousView[6];
    [[vk::offset(2864)]] uint renderPath;
    [[vk::offset(2868)]] uint screenCount;
    [[vk::offset(2872)]] uint shadowAmortize;
    [[vk::offset(2876)]] float shadowDistanceScale;
    [[vk::offset(2880)]] uint shadowFadeCount;
    [[vk::offset(2884)]] uint shadowLightReject;
    [[vk::offset(2888)]] uint shadowOwnershipReject;
    [[vk::offset(2892)]] uint shadowSlotCount;
    [[vk::offset(2896)]] int4 shadowSlots;
    [[vk::offset(2912)]] float tanHalfFieldOfView;
    [[vk::offset(2916)]] uint temporal;
    [[vk::offset(2920)]] uint2 tileGrid;
    [[vk::offset(2928)]] uint viewBase;
    [[vk::offset(2932)]] uint _pad2932;
    [[vk::offset(2936)]] uint _pad2936;
    [[vk::offset(2940)]] uint _pad2940;
    [[vk::offset(2944)]] float3 viewForward;
    [[vk::offset(2956)]] uint _pad2956;
    [[vk::offset(2960)]] float3 viewPosition;
    [[vk::offset(2972)]] uint _pad2972;
    [[vk::offset(2976)]] float3 viewRight;
    [[vk::offset(2988)]] uint _pad2988;
    [[vk::offset(2992)]] float3 viewUp;
    [[vk::offset(3004)]] uint viewportCount;
    [[vk::offset(3008)]] uint workCounterRow;
    [[vk::offset(3012)]] uint workCounterRowDetail;
};
[[vk::binding(0, 3)]] ConstantBuffer<SdfMeshPass> passGroup : register(b0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t1, space3);
[[vk::binding(2, 3)]] Texture2D<float4> sdfImpostorDepth : register(t2, space3);
[[vk::binding(3, 3)]] RWStructuredBuffer<uint> workCounters : register(u3, space3);

// The pushed index: Vulkan push constants at offset 0, Direct3D 12 root constants at register b0, space 4.
struct SdfMeshPushedIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<SdfMeshPushedIndex> pushedIndex : register(b0, space4);

// The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
// back): each counted kind in GpuWork.KernelKinds order, march steps, texels written, sky evaluations, sky hashes,
// sky texture loads, then six shadow-slot step counts, secondary-shadow pixels, indirect hits, samples and unresolved work,
// followed by shape evaluations and shape gradients, as a
// 64-bit count in two words, low word first. An interface declaring no work counters declares the same functions
// empty.
static const uint PuckWorkRowWords = 34u;
static const uint PuckWorkStepsWord = 0u;
static const uint PuckWorkTexelsWord = 2u;
static const uint PuckWorkSkyWord = 4u;
static const uint PuckWorkSkyHashesWord = 6u;
static const uint PuckWorkSkyTextureLoadsWord = 8u;
static const uint PuckWorkShadowWord = 10u;
static const uint PuckWorkShadowSlots = 6u;
static const uint PuckWorkShadowPixelsWord = 22u;
static const uint PuckWorkIndirectWord = 24u;
static const uint PuckWorkShapesWord = 30u;
static const uint PuckWorkGradientsWord = 32u;
// Adds to one count: the low word atomically, then the high word by one when that addition carries.
void puckAddWork(uint word, uint amount) {
    if (amount == 0u) {
        return;
    }

    uint before;

    InterlockedAdd(workCounters[word], amount, before);

    if (before > (0xFFFFFFFFu - amount)) {
        InterlockedAdd(workCounters[word + 1u], 1u);
    }
}
// Adds an invocation's march steps and texels written to its pass's row: the wave sums both, and its first active
// lane adds each sum. Every lane that did work reaches the call, since a lane that returned before it counts nothing.
void puckCountWork(uint steps, uint texels) {
    uint waveSteps = WaveActiveSum(steps);
    uint waveTexels = WaveActiveSum(texels);

    if (WaveIsFirstLane()) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}
// Named rows are disjoint from the plain pass row; the ledger sums both once the submission completes.
// Per-lane atomics permit divergent layer evaluation without merging lanes targeting different rows.
void puckCountDetail(uint detail, uint steps, uint texels, uint evaluations, uint hashes, uint loads) {
    if (passGroup.workCounterRowDetail == 0u) {
        return;
    }
    uint row = ((passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
    puckAddWork((row + PuckWorkStepsWord), steps);
    puckAddWork((row + PuckWorkTexelsWord), texels);
    puckAddWork((row + PuckWorkSkyWord), evaluations);
    puckAddWork((row + PuckWorkSkyHashesWord), hashes);
    puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);
}
void puckCountShapes(uint shapes, uint gradients) {
    uint waveShapes = WaveActiveSum(shapes);
    uint waveGradients = WaveActiveSum(gradients);
    if (WaveIsFirstLane()) {
        uint row = passGroup.workCounterRow * PuckWorkRowWords;
        puckAddWork(row + PuckWorkShapesWord, waveShapes);
        puckAddWork(row + PuckWorkGradientsWord, waveGradients);
    }
}
// Each invocation names its level or proof detail; those rows sum into the pass once at readback.
void puckCountIndirect(uint detail, uint hits, uint samples, uint unresolved) {
    uint row = ((passGroup.workCounterRowDetail == 0u)
        ? passGroup.workCounterRow
        : (passGroup.workCounterRowDetail + detail)) * PuckWorkRowWords;
    puckAddWork((row + PuckWorkIndirectWord), hits);
    puckAddWork((row + PuckWorkIndirectWord + 2u), samples);
    puckAddWork((row + PuckWorkIndirectWord + 4u), unresolved);
}
// Each invocation owns its slot delta. A divergent march need not reconverge its subgroup before a
// reduction and a separate election on Vulkan (SPIR-V Uniform Control Flow).
void puckCountShadow(uint slot, uint steps) {
    if (slot < PuckWorkShadowSlots) {
        puckAddWork(((passGroup.workCounterRow * PuckWorkRowWords) + PuckWorkShadowWord + (slot * 2u)), steps);
    }
}
// One secondary lit pixel belongs to one decision. Its march and slot counts are a partition of the pass,
// while its pixel count exposes rejections and reuse even when a march takes zero field samples.
void puckCountShadowDecision(uint detail, uint slot, uint steps) {
    uint row = ((passGroup.workCounterRowDetail == 0u ? passGroup.workCounterRow : passGroup.workCounterRowDetail + detail) * PuckWorkRowWords);
    puckAddWork(row + PuckWorkShadowPixelsWord, 1u);
    puckAddWork(row + PuckWorkStepsWord, steps);
    puckAddWork(row + PuckWorkShadowWord + (slot * 2u), steps);
}
// Adds a fragment's march steps and texels written to its pass's row: the wave sums its lanes that are not helper
// lanes, and the first of them adds each sum. A helper lane counts nothing and never adds, whether or not the
// backend lets it take part in wave operations, since its atomics have no effect.
void puckCountFragmentWork(uint steps, uint texels) {
    bool counting = !IsHelperLane();
    uint waveSteps = WaveActiveSum(counting ? steps : 0u);
    uint waveTexels = WaveActiveSum(counting ? texels : 0u);

    if (counting && (WavePrefixCountBits(counting) == 0u)) {
        uint row = (passGroup.workCounterRow * PuckWorkRowWords);

        puckAddWork((row + PuckWorkStepsWord), waveSteps);
        puckAddWork((row + PuckWorkTexelsWord), waveTexels);
    }
}

#endif // PUCK_SHADER_INTERFACE_SDF_MESH
