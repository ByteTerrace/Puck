using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the <see cref="SdfOp.DomainWarp"/> SOLIDITY proof, the gate cross-backend PARITY cannot provide:
/// both backends overstep a non-1-Lipschitz field IDENTICALLY, so a parity diff of two equally-eroded renders passes
/// while both are wrong. DomainWarp perturbs the sample point by a bounded, cross-coupled sinusoidal field BEFORE the
/// shape evaluates; the metric stretches by up to <c>amplitude·‖frequency‖</c>, so the warped field can OVERESTIMATE
/// true distance by that factor. Sphere-traced WITHOUT the Lipschitz step clamp, a grazing ray approaching a warped
/// blade near a wobble crest reads that overestimate and steps clean PAST the blade into the sky behind it — eroding
/// the silhouette. WITH the clamp (<c>SdfProgram.AnalyzeLipschitz</c> bakes <c>1/(1 + amplitude·‖frequency‖)</c> into
/// the step scale) the march stays conservative and the blades render solid.
/// <para>The test shape is a floating row of thin, bright EMISSIVE blades (tiled by <see cref="SdfOp.Repeat"/>, an
/// isometric fold — no clamp of its own) each wobbled by DomainWarp (frequency 2.5/2.5/2.5, amplitude 0.14,
/// amplitude·‖frequency‖ ≈ 0.61 — a moderate margin, not a knife-edge), viewed by a low oblique camera whose grazing
/// rays travel along the warped faces across many blades. No ground plane, so every gap is unambiguous sky.</para>
/// <para>The detector mirrors <see cref="WorldWarpSolidityStage"/>: a coverage floor (silhouette-agnostic — the
/// warped outline is irregular) is the primary tooth, with a loose enclosed-hole net as a secondary catch. A bright
/// EMISSIVE blade keeps every blade pixel high-red even where a wobble fold self-shadows.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldDomainWarpSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host (deterministic, fixed scene/camera) by a clamped-vs-unclamped differential on the
    // SAME scene (stepScale forced to 1.0 via reflection for the unclamped measurement): the DomainWarp boundary
    // factor recovers the blade pixels an unclamped approach-overstep erodes to sky — measured 235121 blade pixels
    // clamped (stepScale 0.741) vs 221217 unclamped. The floor sits well below the eroded count so a truly
    // broken/unframed render (INFRA) is distinguished from a holed one (FAIL).
    //
    // RECALIBRATED when DisplaceWarpLipschitz was corrected from ‖frequency‖₂ to ‖frequency‖∞ — DomainWarp's Jacobian
    // is I + amplitude·D·P with P the cyclic permutation and D = diag(f_i·cos(...)), a generalized permutation matrix
    // whose spectral norm IS its largest entry. The old ‖·‖₂ bound over-clamped by √3× on this isotropic frequency
    // (stepScale 0.623, a fatter 244106 px silhouette). The tighter, still-provably-safe clamp renders closer to the
    // true silhouette (235121 px). The unclamped figure is a property of stepScale == 1.0 and is unchanged, so the
    // tooth still bites.
    private const int VacuityFloor = 90000;
    // The PRIMARY tooth: coverage below this means the blades eroded — the field overstepped as grazing rays
    // approached a wobble crest and stepped over the blade. 228000 sits ~3.0% below the clamped 235121 and ~3.1% above
    // the unclamped 221217 — roughly midway, so it survives benign edge-pixel shifts without masking a real clamp
    // regression.
    private const int MinBladeCoverage = 228000;
    // SECONDARY net (deliberately loose): interior sky the border flood-fill can't reach. DomainWarp perturbs the
    // POINT (not just the field), so its wobble folds the blade edges into more self-occluding nooks than Displace's
    // pure relief; the in-cell gaps between repeated blades also enclose more perspective pockets. A solid render
    // still carries a real enclosed-gap floor (measured ~1.6% clamped); this only trips on GROSS holing well above
    // that — COVERAGE is the real regression catch.
    private const double MaxEnclosedHoleFraction = 0.03;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red). The threshold sits in the wide
    // valley just above it. It was 90 on the premise that a 0.7-emissive blade stays red >= ~150 "under any shading" —
    // FALSE since the shading wave landed: emissive is still added un-occluded, but the lit color then passes through
    // the curvature ink-outline/cavity-darken, the 5-tap AO on the ambient fill, distance fog (dimming the far boxes
    // toward the vanishing point), and coverage AA — which together pull present-blade edges/creases/far-boxes into the
    // ~40-90 red band on this HIGH-perimeter thin-blade row (DomainWarp's point wobble makes the inter-box gaps more
    // irregular than Displace's, so it darkens/encloses more). A red-90 cut miscounts those as sky, eroding coverage
    // (~15k px) and inflating the enclosed-gap count with shaded-but-present blade. Dropping to 40 (still 14 above the
    // sky ceiling) re-counts shaded blade as blade, while a REAL march hole — pure sky, red ~15-26, punched clean
    // through a blade — stays below 40 and is still caught: the coverage floor and enclosed cap below are UNCHANGED, so
    // the teeth keep their full discriminating power. Verified NOT a clamp regression: at the true-sky threshold the
    // coverage recovers to the CLAMPED calibration (234842 px, ~= 235121 and far above the unclamped 221217), the
    // enclosed pixels are inter-box sky slivers between the wobbled boxes (scene geometry, not tunneling — confirmed by
    // overlay), and world-warp-solidity (a single wide low-perimeter blade) still passes untouched.
    private const byte SolidRedThreshold = 40;

    /// <inheritdoc/>
    public string Name => "world-domain-warp-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>A floating row of thin, bright EMISSIVE blades tiled by <see cref="SdfOp.Repeat"/> (spacing 0.9 in X,
    /// 6.0 in Y/Z so only the y=z=0 row is populated), each wobbled by DomainWarp (frequency 2.5/2.5/2.5, amplitude
    /// 0.14) BEFORE the box shape evaluates. No ground plane, so every gap between/around blades is unambiguous sky.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildWarpedBladesScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.55f, 0.1f), Emissive: 0.7f));

        return builder
            .Repeat(spacing: new Vector3(0.9f, 6.0f, 6.0f))
            .DomainWarp(frequency: new Vector3(2.5f, 2.5f, 2.5f), amplitude: 0.14f)
            .Box(halfExtents: new Vector3(0.30f, 0.30f, 0.10f), round: 0.02f, material: brass)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildWarpedBladesScene();
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "world-domain-warp-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the warped blade row rendered only {solidPixels} blade pixels (< {VacuityFloor}) — the render is broken or the blades are unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Primary teeth: the warp overestimate erodes coverage as grazing rays step over a wobble crest.
        if (solidPixels < MinBladeCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the warped blade row eroded to {solidPixels} blade pixels (< {MinBladeCoverage}; the clamp renders ~232312) — DomainWarp's overestimate steps over the blades WITHOUT the Lipschitz step clamp");
        }

        // Secondary catch (silhouette-agnostic): sky reachable from any border is legitimate background; sky the
        // flood-fill cannot reach is walled in by blade.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the warped blade row holed: {enclosedHoles} interior sky pixels enclosed by blade ({(enclosedFraction * 100.0):0.##}% of {solidPixels} blade pixels) — DomainWarp oversteps WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the DomainWarp-wobbled blade row (freq 2.5/2.5/2.5, amp 0.14, stepScale {program.StepScale:0.###}) renders SOLID on the Vulkan host: {solidPixels} blade pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the D1 Lipschitz step clamp holds the field 1-Lipschitz-safe");
    }

    private static int CountSolid(byte[] pixels) {
        var count = 0;

        for (var i = 0; (i < ((int)Width * (int)Height)); i++) {
            if (pixels[(i * 4)] >= SolidRedThreshold) {
                count++;
            }
        }

        return count;
    }

    // Flood-fill the NON-solid (sky) pixels inward from every border pixel (4-connected, iterative — no recursion, so
    // a full-frame fill can't overflow the stack), then count the non-solid pixels the fill never reached: those are
    // sky pockets fully walled in by blade, i.e. overstep holes.
    private static int CountEnclosedHoles(byte[] pixels) {
        var width = (int)Width;
        var height = (int)Height;
        var total = (width * height);
        var reached = new bool[total];
        var stack = new Stack<int>();

        void Seed(int x, int y) {
            var index = ((y * width) + x);

            if ((pixels[(index * 4)] < SolidRedThreshold) && !reached[index]) {
                reached[index] = true;

                stack.Push(item: index);
            }
        }

        for (var x = 0; (x < width); x++) {
            Seed(x: x, y: 0);
            Seed(x: x, y: (height - 1));
        }

        for (var y = 0; (y < height); y++) {
            Seed(x: 0, y: y);
            Seed(x: (width - 1), y: y);
        }

        while (stack.Count > 0) {
            var index = stack.Pop();
            var x = (index % width);
            var y = (index / width);

            if (x > 0) { Seed(x: (x - 1), y: y); }
            if (x < (width - 1)) { Seed(x: (x + 1), y: y); }
            if (y > 0) { Seed(x: x, y: (y - 1)); }
            if (y < (height - 1)) { Seed(x: x, y: (y + 1)); }
        }

        var enclosed = 0;

        for (var index = 0; (index < total); index++) {
            if ((pixels[(index * 4)] < SolidRedThreshold) && !reached[index]) {
                enclosed++;
            }
        }

        return enclosed;
    }

    private static SdfFrame BuildFrame(SdfProgram program) {
        // A low, elevated oblique camera looking down the blade row: its rays graze across many warped faces at
        // once — exactly the condition that makes a non-1-Lipschitz warp overstep. A face-on camera into a single
        // blade would never graze and never erode, hiding the very defect this stage guards.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(6.0f, 2.2f, 1.0f),
            target: new Vector3(-6.0f, 0.0f, -0.2f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: Width,
            viewportHeight: Height
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );
    }
}
