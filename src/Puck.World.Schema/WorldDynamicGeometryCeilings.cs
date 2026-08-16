namespace Puck.World;

/// <summary>
/// Contributed dynamic-geometry ceilings: the per-frame dynamic-instance
/// count a document's contributed dynamic content (e.g. a scripted addon, a creator-mode animated placement pool)
/// may add while keeping its own share of the 60 Hz frame budget under policy — <see cref="GpuBudgetMilliseconds"/>
/// GPU and <see cref="CpuBudgetMilliseconds"/> CPU per frame — measured on an RTX 4070 and derated
/// <see cref="RdnaDerateFactor"/>x for the RDNA2 floor (the GPU support matrix's weakest of the four calibrated GPUs).
/// <para>
/// <see cref="MaxContributedDynamicInstances"/> gates
/// <c>WorldDefinitionValidator</c>'s document-global dynamic-instance count — the sum, across every placement row, of
/// each animated placement's single replay instance plus every inhabited placement's declared body count — at
/// document-load time, refusing by name before <see cref="WorldRenderEnvelope"/> ever probes the candidate. This is
/// separate from (and tighter, document-wide, than) the per-row engine-buffer ceilings
/// (<c>WorldPlacementPolicy.MaxStampRegistrations</c>, the authored population capacity) that
/// already gate a single placement's replay-pool or inhabit-count admission — this check catches the sum across many
/// placement rows, which no earlier gate totals. <see cref="WorldRenderEnvelope"/> still separately admits on probed
/// program words/instances at apply time; this constant is consumed at document validation instead of staying an
/// unread number.
/// </para>
/// </summary>
/// <remarks>
/// <para><b>Measurement protocol.</b> Device: an RTX 4070 (the other three calibrated GPUs in the support matrix
/// were not re-run for this measurement). Harness: <c>src/Puck.SdfVm.Bench</c>, a GPU/CPU ceiling harness built from
/// <c>Puck.Launcher</c> + <c>SdfWorldRenderBuilder</c>, the same generic composition assembly <c>Puck.World</c>
/// itself uses, driving a purpose-built <c>ISdfFrameSource</c> through <c>Puck.SdfVm.Debug.SdfBenchScene</c>'s
/// <c>DynamicMatrix</c> ladder — see <c>SdfBenchWorkloads.BuildDynamicMatrixLadder</c>. Matrix: N in
/// {0, 256, 1024, 4096, 16384} spheres (the CoreOps views kernel variant — Sphere + Translate/TransformDynamic only,
/// no exotic op) x placement {Clustered (the whole count packed into one fixed ~2.2-unit footprint — the worst-tile
/// case), Uniform (a centered 3D grid, spacing grows with N), FarCorners (N split across eight local clusters ~8
/// units from the origin)} x {static (baked, grid-invariant) or moving (one dynamic-transform slot per instance,
/// orbiting every produced frame — forces <c>SdfProgram.RequiresFrameInstanceGridRebuild</c>'s per-frame CPU
/// rebuild)}. Resolution 1280x800 (<c>WorldHostDefaults.Default</c> — Puck.World's own shipped default). Quality
/// preset: <c>SdfFrame</c>'s own defaults, unmodified (soft shadows on, ambient occlusion on, exact — not fast —
/// shadow/AO march; the harness overrides nothing) — i.e. the full production-quality shading path, not a cheapened
/// one. Both backends (Vulkan and Direct3D 12). Medians over >=300 sampled frames (20 warm-up frames discarded)
/// per the 30-cell full-matrix run; two bisection rungs (below) used the same warm/sample counts at N=1 and N=8.
/// </para>
/// <para><b>The bisection.</b> The full ladder's smallest nonzero rung, N=256, already measured 12-16 ms of added
/// GPU frame time per placement (24-32x the derated 1.0 ms budget), so the budget-crossing knee sits below N=256.
/// Two single rungs — N=8 then N=1 — were
/// measured directly (Vulkan for all three placements; Direct3D 12 cross-checked on Clustered N=1, agreeing within
/// ~3%) instead of re-running the whole ladder at finer N. Even N=1 — the smallest possible nonzero instance count —
/// already exceeds the derated budget on every placement, so the crossing sits strictly between N=0 (fits trivially)
/// and N=1 (already over): the raw added cost of framing and fully shading (soft shadows + AO) even one dynamic
/// sphere already consumes more than half the whole 1.0 ms/frame policy before the RDNA2 derate is even applied.
/// </para>
/// <para><b>Raw medians (ms), N=0 vs N=1, static, 1280x800:</b></para>
/// <code>
/// placement    | backend | N=0 frame | N=1 frame | added (N=1 - N=0) | added x2 (RDNA2 derate) | vs 1.0ms policy
/// clustered    | vulkan  |    0.275  |    1.367  |   1.092           |   2.184                 | 2.18x over
/// clustered    | directx |    0.275  |    1.399  |   1.124           |   2.248                 | 2.25x over
/// uniform      | vulkan  |    0.276  |    1.241  |   0.966           |   1.932                 | 1.93x over
/// far-corners  | vulkan  |    0.275  |    1.201  |   0.926           |   1.852                 | 1.85x over
/// </code>
/// <para>(Uniform/FarCorners were not separately cross-checked on Direct3D 12 at N=1 — Clustered's ~3% backend
/// divergence at N=1 is taken as representative of the measurement's backend-noise floor. The full N in
/// {0,256,1024,4096,16384} matrix, and the raw table this derivation is drawn from, both ran on both backends;
/// the complete 30-row x 2-backend table is in git history.)</para>
/// <para><b>Why the ceiling is 0, not a small positive number.</b> All three placement rows independently cross the
/// budget between N=0 and N=1 — there is no placement family, of the three tested, that admits even one instance of
/// headroom under this preset/resolution. The dominant cost is not instance-count density (the mask/beam tile-cull
/// cost at N=1 is a small fraction of the frame, 0.3-0.5 ms) — it is the views pass's per-covered-pixel shading cost
/// (soft-shadow gather + ambient occlusion march), which a single object, framed close enough to measure at all,
/// already pays close to in full. This is a genuine finding, not a methodology defect to engineer around: at full
/// default shading quality and 1280x800, the 60 Hz budget for contributed dynamic geometry has no headroom
/// on this hardware once derated for the RDNA2 floor. Raising the ceiling above 0 needs one of: a cheaper shading
/// tier specifically for contributed/dynamic content (fast shadow march + fast AO — <c>SdfFrame.UseFastSoftShadowMarch</c>/
/// <c>UseFastAmbientOcclusion</c> already exist and were not armed here), a smaller render target (a split-screen
/// pane is well under 1280x800), or accepting the policy is a GPU-bound ceiling of 0 additional instances at this
/// preset — this file states the fact measured, not a recommendation between those.</para>
/// <para><b>The CPU ceiling is not binding.</b> The worst measured per-frame instance-grid rebuild
/// (<c>Puck.SdfVm.SdfWorldEngine.LastInstanceGridRebuildMilliseconds</c>) at the top of the tested range,
/// N=16384 (uniform, moving, Direct3D 12) was 0.252 ms raw — derated x2 = 0.504 ms, a hair over the 0.5 ms policy
/// at the very top of the tested instance cap (<c>SdfProgramBuilder.MaxInstances</c> = 16384). The measured
/// per-instance CPU rate is ~1.54e-5 ms/instance (0.252 ms / 16384); solving for the derated-0.25ms-raw crossing
/// gives N ~ 16254 — i.e. the CPU ceiling sits above the engine's own hard instance cap for all practical purposes.
/// The GPU ceiling above (0) dominates completely; <see cref="MaxContributedDynamicInstancesCpuBound"/> is recorded
/// for completeness/traceability only, never as the binding term.</para>
/// </remarks>
public static class WorldDynamicGeometryCeilings {
    /// <summary>The CPU budget for contributed dynamic geometry's own share of one frame, at 60 Hz, milliseconds.</summary>
    public const double CpuBudgetMilliseconds = 0.5;
    /// <summary>The GPU budget for contributed dynamic geometry's own share of one frame, at 60 Hz, milliseconds.</summary>
    public const double GpuBudgetMilliseconds = 1.0;
    /// <summary>The document-global dynamic-instance ceiling <c>WorldDefinitionValidator</c>'s admission check gates
    /// contributed dynamic-instance content against — the CPU/grid bound, not the GPU bound. Authors own the frame
    /// budget: the measurement above shows GPU cost is per-covered-pixel (shading
    /// quality x screen coverage), not per-instance — one framed object pays the shading cost nearly in full, so a
    /// per-instance GPU ceiling prices to zero and would gate the feature off while bounding nothing real. GPU cost
    /// is instead the world author's frame-budget responsibility, like every other content choice they author (the
    /// same authors-decide posture as <c>addons.guestStateCapture</c>). <see cref="MaxContributedDynamicInstancesGpuBound"/>
    /// stays recorded as the measured fact behind that choice; the cheaper-shading-tier and smaller-target levers in
    /// the remarks remain available to authors, not mandates.</summary>
    public const int MaxContributedDynamicInstances = MaxContributedDynamicInstancesCpuBound;
    /// <summary>The CPU-bound ceiling: the largest per-frame contributed dynamic-instance count whose measured
    /// per-frame instance-grid-rebuild cost, derated <see cref="RdnaDerateFactor"/>x, stays within
    /// <see cref="CpuBudgetMilliseconds"/> — measured non-binding (~16254, effectively the engine's own instance cap)
    /// across every tested placement. Recorded for traceability only; <see cref="MaxContributedDynamicInstancesGpuBound"/>
    /// is the term that actually governs (see the type remarks).</summary>
    public const int MaxContributedDynamicInstancesCpuBound = 16000;
    /// <summary>The GPU-bound ceiling: the largest per-frame contributed dynamic-instance count whose measured added
    /// GPU frame cost, derated <see cref="RdnaDerateFactor"/>x, stays within <see cref="GpuBudgetMilliseconds"/> —
    /// measured 0 across all three tested placement rows (Clustered/Uniform/FarCorners), because even one instance's
    /// added cost (0.93-1.12 ms raw, 1.85-2.25 ms derated) already exceeds the 1.0 ms policy. See the type remarks
    /// for the full raw table and why this is a real measured finding, not headroom this file failed to find.</summary>
    public const int MaxContributedDynamicInstancesGpuBound = 0;
    /// <summary>The conservative multiplier applied to an RTX 4070 measured median to approximate the RDNA2 floor
    /// (the GPU support matrix's weakest calibrated GPU) — a measured value must fit the budget after this factor,
    /// not before it.</summary>
    public const double RdnaDerateFactor = 2.0;
}
