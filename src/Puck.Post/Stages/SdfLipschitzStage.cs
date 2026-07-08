using System.Numerics;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. The fast CPU proof that the D1 Lipschitz keystone is COMPUTED and BAKED: SdfProgram.AnalyzeLipschitz
/// derives a per-program step scale (1/L) and packs it into the segment-directory header's free .y lane, and
/// SdfProgram.StepScale reads it back from the packed words (the same lane mapCore consumes in sdf-vm.hlsli). It builds
/// a battery of programs and asserts the baked step scale is:
/// <list type="bullet">
///   <item>EXACTLY 1.0f for the warp-free hero scene — the isometric byte-identical invariant (an isometric scene must
///   multiply its final distance by a bit-exact 1.0, so not one pixel moves);</item>
///   <item>&lt; 1.0f (at each feature's expected factor) for every non-1-Lipschitz source in isolation: a steep twist
///   (the liar's spiral, rate 3.0); a 4:1 eccentric ellipsoid (≈ 1/4); a log-spherical fold (≈ 1/√2); a CellJitter
///   boundary; a chamfer blend (≈ 1/√2, the bevel's acute-corner gradient); a Displace relief and a DomainWarp
///   (≈ 1/2 at amp·‖freq‖ = 1, the sinusoidal metric-stretch).</item>
/// </list>
/// The GPU CONSEQUENCE (a clamped warp/relief renders solid instead of holing) is the world-*-solidity stages; this is
/// the cheap CPU proof of the compiler fact underneath them. A pure-CPU self-test, no GPU required.
/// </summary>
internal sealed class SdfLipschitzStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "sdf-lipschitz";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // 1. Warp-free / eccentricity-free: the step scale must be EXACTLY 1.0f (bit-for-bit) or an isometric scene
        // would re-roll. Compare the bits, not the value, so a 0.99999994 can never sneak through as "close enough".
        var heroStepScale = WorldStage.BuildHeroScene().StepScale;

        if (BitConverter.SingleToUInt32Bits(value: heroStepScale) != BitConverter.SingleToUInt32Bits(value: 1.0f)) {
            return PostStageOutcome.Fail(detail: $"the warp-free hero scene baked stepScale {heroStepScale:R} (0x{BitConverter.SingleToUInt32Bits(value: heroStepScale):X8}), not exactly 1.0f — the isometric byte-identical invariant is broken");
        }

        // 2. A steep twist (the liar's spiral, rate 3.0) must clamp: 0 < stepScale < 1.
        var twistStepScale = WorldWarpSolidityStage.BuildLiarsSpiralScene().StepScale;

        if ((twistStepScale <= 0.0f) || (twistStepScale >= 1.0f)) {
            return PostStageOutcome.Fail(detail: $"the steep-twist liar's spiral baked stepScale {twistStepScale:R}, expected 0 < stepScale < 1 (a non-1-Lipschitz warp must be clamped)");
        }

        // 3. A 4:1 eccentric ellipsoid must clamp to ≈ 1/eccentricity = 1/4 (the shape-approx factor in isolation).
        var ellipsoidStepScale = BuildEccentricEllipsoidScene().StepScale;

        if ((ellipsoidStepScale <= 0.0f) || (ellipsoidStepScale >= 1.0f)) {
            return PostStageOutcome.Fail(detail: $"the 4:1 eccentric ellipsoid baked stepScale {ellipsoidStepScale:R}, expected 0 < stepScale < 1");
        }

        if (MathF.Abs(ellipsoidStepScale - 0.25f) > 0.01f) {
            return PostStageOutcome.Fail(detail: $"the 4:1 eccentric ellipsoid baked stepScale {ellipsoidStepScale:R}, expected ≈ 0.25 (1 / eccentricity 4)");
        }

        // 4. A log-spherical fold (shellRatio 2.0) must clamp to ≈ 1/exp(w/2) = 1/exp(ln(2)/2) = 1/√2 ≈ 0.707 — the
        // shell-boundary metric-distortion factor in isolation (the D2 twin of the twist assertion, under the GPU
        // world-log-sphere-solidity consequence).
        var logSphereStepScale = WorldLogSphereSolidityStage.BuildDrosteTunnelScene().StepScale;

        if ((logSphereStepScale <= 0.0f) || (logSphereStepScale >= 1.0f)) {
            return PostStageOutcome.Fail(detail: $"the shellRatio-2.0 log-spherical fold baked stepScale {logSphereStepScale:R}, expected 0 < stepScale < 1 (a non-1-Lipschitz domain warp must be clamped)");
        }

        var expectedLogSphereStepScale = (1f / MathF.Exp(0.5f * MathF.Log(2.0f)));

        if (MathF.Abs(logSphereStepScale - expectedLogSphereStepScale) > 0.01f) {
            return PostStageOutcome.Fail(detail: $"the shellRatio-2.0 log-spherical fold baked stepScale {logSphereStepScale:R}, expected ≈ {expectedLogSphereStepScale:0.###} (1 / exp(w/2), w = ln 2)");
        }

        // 5. A CellJitter fold (the solidity gate's own blade scene) must clamp: 0 < stepScale < 1 — the boundary
        // step factor L_cj = sqrt((min(spacing)/2 + jitter/2)^2 + 2*jitter^2) / m in isolation (the D1 twin of the twist
        // and log-spherical asserts, under the GPU world-cell-jitter-solidity consequence). The exact value tracks the
        // gate scene's spacing/jitter/blade reach, so this asserts the clamp is present and in a sane band, not a pinned
        // constant that would fight the gate's scene tuning.
        var cellJitterStepScale = WorldCellJitterSolidityStage.BuildJitteredBladesScene().StepScale;

        if ((cellJitterStepScale <= 0.0f) || (cellJitterStepScale >= 1.0f)) {
            return PostStageOutcome.Fail(detail: $"the CellJitter blade scene baked stepScale {cellJitterStepScale:R}, expected 0 < stepScale < 1 (the cell-boundary discontinuity must be clamped)");
        }

        // 6. A chamfer blend must clamp to ≈ 1/√2 ≈ 0.707 — the bevel plane's acute-corner √2 gradient in isolation
        // (chamfer is the one blend op that is NOT 1-Lipschitz; smooth-min stays 1). The GPU consequence is world-chamfer.
        var chamferStepScale = BuildChamferScene().StepScale;
        var expectedChamferStepScale = (1f / MathF.Sqrt(2.0f));

        if (MathF.Abs(chamferStepScale - expectedChamferStepScale) > 0.01f) {
            return PostStageOutcome.Fail(detail: $"the chamfer-union scene baked stepScale {chamferStepScale:R}, expected ≈ {expectedChamferStepScale:0.###} (1 / √2 — the bevel's acute-corner gradient)");
        }

        // 7. A Displace field op (freq |·| = 4, amp 0.25 → amp·‖freq‖ = 1) must clamp to ≈ 1/(1 + 1) = 0.5 — the
        // sinusoidal relief's metric-stretch factor in isolation. The GPU consequence is world-displace-solidity.
        var displaceStepScale = BuildDisplaceScene().StepScale;

        if (MathF.Abs(displaceStepScale - 0.5f) > 0.01f) {
            return PostStageOutcome.Fail(detail: $"the Displace scene baked stepScale {displaceStepScale:R}, expected ≈ 0.5 (1 / (1 + amp·‖freq‖ = 1))");
        }

        // 8. A DomainWarp point op (same amp·‖freq‖ = 1) must clamp to ≈ 0.5 — the warp's Jacobian metric-stretch in
        // isolation (with no downstream twist/bend, its reach fold is inert). The GPU consequence is world-domain-warp-solidity.
        var domainWarpStepScale = BuildDomainWarpScene().StepScale;

        if (MathF.Abs(domainWarpStepScale - 0.5f) > 0.01f) {
            return PostStageOutcome.Fail(detail: $"the DomainWarp scene baked stepScale {domainWarpStepScale:R}, expected ≈ 0.5 (1 / (1 + amp·‖freq‖ = 1))");
        }

        return PostStageOutcome.Pass(detail: $"warp-free stepScale == 1.0f exactly; steep twist {twistStepScale:0.###}, 4:1 ellipsoid {ellipsoidStepScale:0.###}, log-spherical fold {logSphereStepScale:0.###}, CellJitter blades {cellJitterStepScale:0.###}, chamfer {chamferStepScale:0.###}, Displace {displaceStepScale:0.###}, DomainWarp {domainWarpStepScale:0.###} all < 1 — the per-program Lipschitz factor is computed and baked into the segment-directory header");
    }

    // A box chamfer-unioned to a sphere: the ChamferUnion bevel's √2 acute-corner gradient is the ONLY Lipschitz
    // contributor (no warp, no eccentric shape), so stepScale = 1/√2 — the cleanest isolation of the chamfer path.
    private static SdfProgram BuildChamferScene() {
        var builder = new SdfProgramBuilder();
        var steel = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.6f, 0.62f, 0.68f)));

        return builder
            .Box(halfExtents: new Vector3(0.6f, 0.6f, 0.6f), round: 0f, material: steel)
            .Translate(offset: new Vector3(0.7f, 0f, 0f))
            .Sphere(radius: 0.6f, material: steel, blend: SdfBlendOp.ChamferUnion, smooth: 0.3f)
            .Build();
    }

    // A sphere with a Displace field op (frequency (4,0,0) → ‖freq‖ = 4, amplitude 0.25 → amp·‖freq‖ = 1): the
    // relief's metric-stretch (1 + 1) is the only contributor, so stepScale = 0.5.
    private static SdfProgram BuildDisplaceScene() {
        var builder = new SdfProgramBuilder();
        var clay = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.5f, 0.3f)));

        return builder
            .Sphere(radius: 1.0f, material: clay)
            .Displace(frequency: new Vector3(4.0f, 0f, 0f), amplitude: 0.25f)
            .Build();
    }

    // A DomainWarp point op (same amp·‖freq‖ = 1) then a sphere: the warp's metric-stretch (1 + 1) is the only
    // contributor (no downstream twist/bend, so the reach fold is inert), so stepScale = 0.5.
    private static SdfProgram BuildDomainWarpScene() {
        var builder = new SdfProgramBuilder();
        var clay = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.5f, 0.8f)));

        return builder
            .DomainWarp(frequency: new Vector3(4.0f, 0f, 0f), amplitude: 0.25f)
            .Sphere(radius: 1.0f, material: clay)
            .Build();
    }

    // A lone 4:1 ellipsoid (radii 4:1:1): eccentricity 4 → stepScale = 1/4. No warp, so the ellipsoid approximation
    // factor is the ONLY contributor — the cleanest isolation of the shape-approx path.
    private static SdfProgram BuildEccentricEllipsoidScene() {
        var builder = new SdfProgramBuilder();
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.3f, 0.7f, 0.4f)));

        return builder
            .Ellipsoid(radii: new Vector3(4.0f, 1.0f, 1.0f), material: jade)
            .Build();
    }
}
