namespace Puck.Post;

/// <summary>The ordered POST stage registry. The battery runs these in declaration order (tiers A→D); the
/// <c>--tier</c> and <c>--filter</c> options select a subset. Stages are added here as each milestone lands.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <param name="fuzzSeed">An override for <see cref="FuzzStage"/>'s fixed deterministic seed list (the
    /// <c>--fuzz-seed</c> CLI seam), or <see langword="null"/> to keep its default sample.</param>
    /// <returns>The stages, in run order.</returns>
    // Assembled from per-tier helpers rather than one flat collection expression: each helper constructs only its own
    // tier's stages, so the coupling CA1506 counts (one edge per stage type) stays distributed across four methods
    // instead of piling onto Create as the registry grows.
    public static IReadOnlyList<IPostStage> Create(int? fuzzSeed = null) {
        return [
            .. CreateTierA(),
            .. CreateTierB(),
            .. CreateTierC(fuzzSeed: fuzzSeed),
            .. CreateTierD(),
        ];
    }

    // Tier A — CPU pre-flight. The self-tests run first: a determinism gate cannot catch a wrong-but-deterministic
    // operation, so correctness is proven before reproducibility.
    private static IReadOnlyList<IPostStage> CreateTierA() {
        return [
            new FixedPointStage(),
            new WorldCoord3Stage(),
            new BinaryIntegerFunctionsStage(),
            new MonotonicPartitionerStage(),
            new SdfLipschitzStage(),
            new DeterminismStage(),
            new WorldFieldEvaluatorDeterminismStage(),
            new CliDeterminismStage(),
            new InputRoutingStage(),
            new BindingPageStage(),
            new BindingSessionStage(),
            new DisplayTimingStage(),
            new GenlockStage(),
            new RunDocumentStage(),
            new ScriptingDeterminismStage(),
            new VictoryGateStage(),
        ];
    }

    // Tier B — same-device GPU smoke on the offscreen Vulkan host, then the compute SDF world pipeline (M3): the full
    // beam → cull-args → views (indirect) → composite chain through the shared SdfWorldEngine (Puck.SdfVm) in its
    // submit-and-wait harness mode.
    private static IReadOnlyList<IPostStage> CreateTierB() {
        return [
            new ComputeStage(),
            new ResampleStage(),
            new ViewportsStage(),
            new PixelateStage(),
            new CaptureStage(),
            new SplitCoverageStage(),
            new DynamicTransformStage(),
            new WorldFieldDriftStage(),
        ];
    }

    // Tier C — cross-backend (M4): the Vulkan host + the shared LUID-matched Direct3D 12 device (lazily created on the
    // first stage; every acquire waits both devices idle — the reset seam). Then fuzz + ray tracing (M5): the
    // differential fuzzer's fixed deterministic seed sample, then the hardware ray-query/DXR parity check
    // (skip-with-note when either device lacks inline ray tracing).
    private static IReadOnlyList<IPostStage> CreateTierC(int? fuzzSeed) {
        return [
            new ExportStage(),
            new ReverseShareStage(),
            new IndirectStage(),
            new WorldStage(),
            new WorldMenagerieStage(),
            new World2DFamilyStage(),
            new WorldWallpaperStage(),
            new WorldWallpaperP4gStage(),
            new WorldCellJitterStage(),
            new WorldCellJitterSolidityStage(),
            new WorldCellJitterFlavorsStage(),
            new WorldRepeatPolarStage(),
            new WorldRepeatLimitedStage(),
            new WorldSymmetryPlaneStage(),
            new WorldScaleStage(),
            new WorldChamferStage(),
            new WorldChamferSolidityStage(),
            new WorldScopeStage(),
            new WorldSmoothIntersectionStage(),
            new WorldMaterialBlendStage(),
            new WorldMaterialSeamStage(),
            new WorldDilateStage(),
            new WorldDisplaceStage(),
            new WorldDomainWarpStage(),
            new WorldDisplaceSolidityStage(),
            new WorldDomainWarpSolidityStage(),
            new WorldWarpStage(),
            new WorldWarpSolidityStage(),
            new WorldAnalyticNormalStage(),
            new WorldBendStage(),
            new WorldLogSphereStage(),
            new WorldLogSphereSolidityStage(),
            new WorldDrosteSolidityStage(),
            new WorldChildStage(),
            new WorldScreenStage(),
            new WorldGlyphStage(),
            new WorldGlyphDecalStage(),
            new WorldSampledRegionStage(),
            new WorldInstancedStage(),
            new WorldSwarmStage(),
            new WorldGridCullStage(),
            new WorldShadowCullStage(),
            new WorldRenderScaleStage(),
            new CameraShareStage(),
            new CaptureShareStage(),
            new WorldDriftMonolithStage(),
            ((fuzzSeed is int seed) ? new FuzzStage(seeds: [seed]) : new FuzzStage()),
            new RtStage(),
        ];
    }

    // Tier D — performance + live-subsystem checks. The GPU-ms budget runs in-process on the healthy Vulkan host FIRST;
    // the device-loss / hot-switch probes run LAST because they deliberately destabilize a device/presenter, each
    // relaunching this executable as an isolated --probe child (the Tier-D isolation decision).
    private static IReadOnlyList<IPostStage> CreateTierD() {
        return [
            new GpuBudgetStage(),
            new PresentCadenceStage(),
            new DeviceLostStage(),
            new HotSwitchStage(),
        ];
    }
}
