using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the <see cref="SdfOp.Displace"/> SOLIDITY proof, the gate cross-backend PARITY cannot provide: both
/// backends overstep a non-1-Lipschitz field IDENTICALLY, so a parity diff of two equally-eroded renders passes while
/// both are wrong. Displace adds a bounded sinusoidal relief to the field (real corrugation, not a normal map); its
/// gradient reaches <c>amplitude·‖frequency‖</c>, so the field can OVERESTIMATE true distance by up to that factor.
/// Sphere-traced WITHOUT the Lipschitz step clamp, a grazing ray approaching a rippled blade near a corrugation crest
/// reads that overestimate and steps clean PAST the blade into the sky behind it — eroding the silhouette. WITH the
/// clamp (<c>SdfProgram.AnalyzeLipschitz</c> bakes <c>1/(1 + amplitude·‖frequency‖)</c> into the step scale) the march
/// stays conservative and the blades render solid.
/// <para>The test shape is a floating row of thin, bright EMISSIVE blades (tiled by <see cref="SdfOp.Repeat"/>, an
/// isometric fold — no clamp of its own) each corrugated by Displace (frequency 2.5/2.5/2.5, amplitude 0.14,
/// amplitude·‖frequency‖ ≈ 0.61 — a moderate margin, not a knife-edge), viewed by a low oblique camera whose grazing
/// rays travel along the rippled faces across many blades. No ground plane, so every gap is unambiguous sky.</para>
/// <para>The detector mirrors <see cref="WorldWarpSolidityStage"/>: a coverage floor (silhouette-agnostic — the
/// rippled outline is irregular) is the primary tooth, with a loose enclosed-hole net as a secondary catch. A bright
/// EMISSIVE blade keeps every blade pixel high-red even where a corrugation fold self-shadows.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldDisplaceSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host (deterministic, fixed scene/camera) by a clamped-vs-unclamped differential on the
    // SAME scene (stepScale forced to 1.0 via reflection for the unclamped measurement): the Displace boundary factor
    // recovers the blade pixels an unclamped approach-overstep erodes to sky — measured 230705 blade pixels clamped
    // (stepScale 0.741) vs 218861 unclamped. The floor sits well below the eroded count so a truly broken/unframed
    // render (INFRA) is distinguished from a holed one (FAIL).
    //
    // RECALIBRATED when DisplaceWarpLipschitz was corrected from ‖frequency‖₂ to ‖frequency‖∞ — the true supremum of
    // the sin-product basis's gradient (its squared norm is MULTILINEAR in the three squared sines, so it maximizes at
    // a cube vertex, and every vertex evaluates to a single frequency component). The old ‖·‖₂ bound was conservative,
    // never wrong: on this isotropic frequency it over-clamped by √3×, giving stepScale 0.623 and a correspondingly
    // FATTER silhouette (239041 px). The tighter, still-provably-safe clamp renders the blades closer to their true
    // silhouette (230705 px) — thinner, not eroded; the enclosed-hole fraction barely moved (0.285% → 0.326%). The
    // unclamped figure is a property of stepScale == 1.0 and is unchanged by the correction, so the tooth still bites.
    private const int VacuityFloor = 90000;
    // The PRIMARY tooth: coverage below this means the blades eroded — the field overstepped as grazing rays
    // approached a corrugation crest and stepped over the blade. 224000 sits ~2.9% below the clamped 230705 and ~2.4%
    // above the unclamped 218861 — roughly midway, so it survives benign edge-pixel shifts without masking a real
    // clamp regression.
    private const int MinBladeCoverage = 224000;
    // SECONDARY net (deliberately loose): interior sky the border flood-fill can't reach. The corrugation's self-
    // occluding folds leave a small enclosed-gap floor even when solid (measured ~0.3% clamped); this only trips on
    // GROSS holing well above that — COVERAGE is the real regression catch.
    private const double MaxEnclosedHoleFraction = 0.01;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red). The threshold sits in the wide
    // valley just above it. It was 90 on the premise that a 0.7-emissive blade stays red >= ~150 "under any shading" —
    // FALSE since the shading wave landed: emissive is still added un-occluded, but the lit color then passes through
    // the curvature ink-outline/cavity-darken, the 5-tap AO on the ambient fill, distance fog (dimming the far boxes
    // toward the vanishing point), and coverage AA — which together pull present-blade edges/creases/far-boxes into the
    // ~40-90 red band on this HIGH-perimeter thin-blade row. A red-90 cut miscounts those as sky, eroding coverage
    // (~10k px) and inventing enclosed "holes" that are really shaded-but-present blade. Dropping to 40 (still 14 above
    // the sky ceiling) re-counts shaded blade as blade, while a REAL march hole — pure sky, red ~15-26, punched clean
    // through a blade — stays below 40 and is still caught: the coverage floor and enclosed cap below are UNCHANGED, so
    // the teeth keep their full discriminating power. Verified NOT a clamp regression: at the true-sky threshold the
    // coverage recovers to the CLAMPED calibration (231210 px, above 230705 and far above the unclamped 218861), the
    // enclosed pixels are inter-box sky slivers (scene geometry, not tunneling — confirmed by overlay), and
    // world-warp-solidity (a single wide low-perimeter blade, few crevices) still passes untouched.
    private const byte SolidRedThreshold = 40;

    /// <inheritdoc/>
    public string Name => "world-displace-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>A floating row of thin, bright EMISSIVE blades tiled by <see cref="SdfOp.Repeat"/> (spacing 0.9 in X,
    /// 6.0 in Y/Z so only the y=z=0 row is populated), each corrugated by Displace (frequency 2.5/2.5/2.5, amplitude
    /// 0.14). No ground plane, so every gap between/around blades is unambiguous sky.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildDisplacedBladesScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.55f, 0.1f), Emissive: 0.7f));

        return builder
            .Repeat(spacing: new Vector3(0.9f, 6.0f, 6.0f))
            .Box(halfExtents: new Vector3(0.30f, 0.30f, 0.10f), round: 0.02f, material: brass)
            .Displace(frequency: new Vector3(2.5f, 2.5f, 2.5f), amplitude: 0.14f)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildDisplacedBladesScene();
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "world-displace-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the displaced blade row rendered only {solidPixels} blade pixels (< {VacuityFloor}) — the render is broken or the blades are unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Primary teeth: the relief overestimate erodes coverage as grazing rays step over a corrugation crest.
        if (solidPixels < MinBladeCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the displaced blade row eroded to {solidPixels} blade pixels (< {MinBladeCoverage}; the clamp renders ~229867) — Displace's relief overestimate steps over the blades WITHOUT the Lipschitz step clamp");
        }

        // Secondary catch (silhouette-agnostic): sky reachable from any border is legitimate background; sky the
        // flood-fill cannot reach is walled in by blade.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the displaced blade row holed: {enclosedHoles} interior sky pixels enclosed by blade ({(enclosedFraction * 100.0):0.##}% of {solidPixels} blade pixels) — Displace oversteps WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the Displace-corrugated blade row (freq 2.5/2.5/2.5, amp 0.14, stepScale {program.StepScale:0.###}) renders SOLID on the Vulkan host: {solidPixels} blade pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the D1 Lipschitz step clamp holds the field 1-Lipschitz-safe");
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
        // A low, elevated oblique camera looking down the blade row: its rays graze across many corrugated faces at
        // once — exactly the condition that makes a non-1-Lipschitz relief overstep. A face-on camera into a single
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
