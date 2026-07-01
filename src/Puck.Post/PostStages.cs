namespace Puck.Post;

/// <summary>The ordered POST stage registry. The battery runs these in declaration order (tiers A→D); the
/// <c>--tier</c> and <c>--filter</c> options select a subset. Stages are added here as each milestone lands.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IReadOnlyList<IPostStage> Create() {
        return [
            // Tier A — CPU pre-flight. The self-tests run first: a determinism gate cannot catch a
            // wrong-but-deterministic operation, so correctness is proven before reproducibility.
            new FixedPointStage(),
            new WorldCoord3Stage(),
            new DeterminismStage(),
            new CliDeterminismStage(),
            new GenlockStage(),
            new RunDocumentStage(),

            // Tier B — same-device GPU smoke on the offscreen Vulkan host.
            new ComputeStage(),
            new ResampleStage(),
            new ViewportsStage(),
            new PixelateStage(),
            new CaptureStage(),

            // Tier B — the compute SDF world pipeline (M3): the full beam → cull-args → views (indirect) → composite
            // chain through the reusable PostWorldRenderer harness.
            new SplitCoverageStage(),
            new DynamicTransformStage(),

            // Tier C — cross-backend (M4): the Vulkan host + the shared LUID-matched Direct3D 12 device
            // (lazily created on the first stage; every acquire waits both devices idle — the reset seam).
            new ExportStage(),
            new ReverseShareStage(),
            new IndirectStage(),
            new WorldStage(),
            new WorldChildStage(),
            new CameraShareStage(),

            // Tier C — fuzz + ray tracing (M5): the differential fuzzer's fixed deterministic seed sample, then the
            // hardware ray-query/DXR parity check (skip-with-note when either device lacks inline ray tracing).
            new FuzzStage(),
            new RtStage(),

            // Tier D — live-subsystem probes (M6), LAST because they deliberately destabilize a device/presenter.
            // Each relaunches this executable as an isolated --probe child (the plan's Tier-D isolation decision).
            new DeviceLostStage(),
            new HotSwitchStage(),
        ];
    }
}
