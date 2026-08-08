using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the <see cref="SdfOp.CellJitter"/> SOLIDITY proof, the gate cross-backend PARITY cannot provide:
/// CellJitter's per-cell <c>round()</c> fold is a HARD boundary where the hashed offset jumps by up to <c>jitter</c>
/// between adjacent cells, so the folded field is DISCONTINUOUS across every cell wall and OVERESTIMATES true distance
/// there. Sphere-traced OVER-RELAXED (omega 1.2) WITHOUT the Lipschitz step clamp, a grazing ray APPROACHING a blade
/// near a cell wall reads that overestimate and steps clean OVER the whole blade — so the blade is MISSED, not merely
/// punctured. Because both backends run the identical field, a parity diff of two equally-eroded renders passes while
/// both are wrong; this stage renders the jittered field on ONE backend (the Vulkan host) and asserts the blades survive.
/// <para>The test shape is a floating lattice of thin, bright EMISSIVE blades tiled by CellJitter (jitter 0.25, tumble
/// 0.6 — every cell reoriented, so the boundary discontinuity is maximally exercised), viewed down the rows by a low,
/// oblique camera whose grazing rays cross many cell walls. WITH the clamp (AnalyzeLipschitz bakes the reach-independent
/// boundary factor <c>sqrt((min(spacing)/2 + jitter/2)^2 + 2*jitter^2)/m</c> into the segment-header step scale; mapCore
/// multiplies its final distance by it) the over-relaxed steps stay conservative across every wall and the blades render
/// SOLID; WITHOUT it the approach-overstep erodes a large fraction of the blade pixels away to sky.</para>
/// <para>The PRIMARY tooth is therefore COVERAGE, silhouette-agnostic (the scattered/tumbled outline is arbitrary, so a
/// bbox is useless): the clamp recovers the tunnel-eroded blade pixels, so a missing clamp drops the bright-blade count
/// far below the floor (measured: ~247k clamped vs ~163k unclamped on the calibration hardware — see the constants). A
/// SECONDARY enclosed-hole net (flood-fill sky inward from the borders, count the sky the fill cannot reach) catches a
/// gross-holing render; it is deliberately LOOSE because CellJitter's overstep erodes rather than punches — the in-cell
/// margin guarantees inter-cell sky gaps that perspective can enclose, so a solid render still carries a small
/// enclosed-gap floor that is NOT tunnelling. A bright EMISSIVE blade keeps every blade pixel high-red under any shading,
/// so a self-shadowed face never reads as sky.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldCellJitterSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host (deterministic, fixed scene/camera) by a clamped-vs-unclamped differential on the
    // SAME scene: the CellJitter boundary factor recovers the blade pixels the unclamped approach-overstep erodes to sky
    // — measured 246738 blade pixels clamped vs 163021 unclamped (stepScale 0.322). The coverage floor sits in that gap
    // so removing/breaking the clamp (stepScale back to 1.0) FAILS loudly while a benign DXC/codegen edge shift does not.
    private const int VacuityFloor = 90000;
    // The PRIMARY tooth: coverage below this means the blades eroded — the field overstepped as grazing rays approached
    // the cell walls and stepped over the blades. 210000 is centred in the ~163k(unclamped)..~247k(clamped) gap (~18%
    // headroom below the clamped count, ~26% above the eroded count).
    private const int MinBladeCoverage = 210000;
    // SECONDARY net (deliberately loose): interior sky the border flood-fill can't reach. CellJitter's overstep ERODES
    // rather than punches, and the in-cell margin leaves inter-cell sky gaps that perspective can enclose, so a SOLID
    // render still carries a small enclosed-gap floor (measured 1.0% clamped, 0.5% unclamped — the metric does NOT
    // separate clamp state here). This threshold only trips on GROSS holing well above that floor; COVERAGE is the real
    // regression catch.
    private const double MaxEnclosedHoleFraction = 0.02;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red); the warm emissive blade is
    // always HIGH red (>= ~150/255 from emissive alone, under any shading). The threshold sits far from both.
    private const byte SolidRedThreshold = 90;

    /// <inheritdoc/>
    public string Name => "world-cell-jitter-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>A floating lattice of thin, bright EMISSIVE blades tiled by CellJitter (jitter 0.25, tumble 0.6). The
    /// large Y spacing populates only the y=0 plane; blades sit ~0.6 wide in 1.9-unit X cells, viewed down the rows so
    /// grazing rays cross many X walls with a jittered neighbour close behind — the condition that makes the round()
    /// boundary discontinuity overstep. In-cell budget: jitter/2 (0.125) + blade bounding radius (~0.461) = ~0.586 &lt;
    /// min(spacing)/2 (0.95), so the ~0.364 margin holds each blade clear of its walls. That margin is DELIBERATELY
    /// MODERATE (not pathological): it yields a boundary step factor ≈3.1 (stepScale ≈0.32 — see
    /// <see cref="SdfOp.CellJitter"/> in AnalyzeLipschitz), so the clamped march has ample step budget (MaxSteps 160) to
    /// reach every blade WITHOUT self-eroding, and the pass is decisive rather than a knife-edge. The blade is thickened
    /// from a hair-thin plate (0.12 vs 0.05 half-depth) so the test isolates the BOUNDARY overstep (the clamp's job) from
    /// pure thin-geometry step-over (a footprint effect the clamp does not address). No ground plane, so every gap is
    /// unambiguous sky. INTERNAL so <c>sdf-lipschitz</c> can assert this exact program's baked step scale is clamped.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildJitteredBladesScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.95f, y: 0.55f, z: 0.1f), Emissive: 0.7f));

        return builder
            .CellJitter(spacing: new Vector3(x: 1.9f, y: 6.0f, z: 6.0f), jitter: 0.25f, seed: 24601u, tumble: 0.6f, materialVariants: 0)
            .Box(halfExtents: new Vector3(x: 0.30f, y: 0.30f, z: 0.12f), round: 0.02f, material: brass)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildJitteredBladesScene();
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-cell-jitter-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the jittered blade lattice rendered only {solidPixels} blade pixels (< {VacuityFloor}) — the render is broken or unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // PRIMARY tooth (coverage): CellJitter's per-cell round() boundary overstep makes a grazing ray approaching a
        // blade near a cell wall step clean OVER it, ERODING the blade to sky. The reach-independent boundary step clamp
        // recovers those pixels; without it coverage collapses far below the floor (measured ~163k unclamped vs ~247k
        // clamped, stepScale ≈0.32 — the constants sit in that gap).
        if (solidPixels < MinBladeCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the jittered blade lattice eroded to {solidPixels} blade pixels (< {MinBladeCoverage}) — CellJitter's per-cell round() boundary overstep steps over the blades WITHOUT the Lipschitz step clamp");
        }

        // SECONDARY net (deliberately loose): interior sky the border flood-fill can't reach. CellJitter ERODES rather
        // than punches, and the in-cell margin leaves inter-cell sky gaps perspective can enclose, so even a SOLID render
        // carries a small enclosed-gap floor (~1%); this only trips on GROSS holing well above it — COVERAGE is the real
        // regression catch.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the jittered blade lattice holed: {enclosedHoles} interior sky pixels enclosed by blade ({(enclosedFraction * 100.0):0.##}% of {solidPixels}) — gross overstep tunnelling WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the CellJitter blade lattice (jitter 0.25, tumble 0.6, stepScale {program.StepScale:0.###}) renders SOLID on the Vulkan host: {solidPixels} blade pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the reach-independent boundary step clamp holds the over-relaxed march conservative across every cell wall");
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
        // An ELEVATED, offset camera looking along the floating row: its rays travel low and oblique across the lattice,
        // crossing SEVERAL cell boundaries in X and Z at grazing angles — exactly the condition that makes the
        // round()-boundary jitter jump overstep. A face-on camera staring into one cell would never cross a boundary and
        // never hole, hiding the very defect this stage guards.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 6.0f, y: 2.2f, z: 1.0f),
            target: new Vector3(x: -6.0f, y: 0.0f, z: -0.2f),
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
