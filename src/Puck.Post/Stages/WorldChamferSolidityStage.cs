using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the CHAMFER √2 SOLIDITY proof, the gate cross-backend PARITY cannot provide (WorldChamferStage gates
/// only that the two backends AGREE; two equally-holed renders agree). The chamfer blends are the one blend family that
/// is NOT 1-Lipschitz: the bevel plane <c>(a ± c ± b)/√2</c> reaches gradient √2 at an ACUTE seam (exactly 1 at a
/// perpendicular one), so a chamfer-composed field can OVERESTIMATE true distance by up to √2 there. Sphere-traced
/// WITHOUT the Lipschitz step clamp, a grazing ray approaching a thin chamfered blade near its acute weld reads that
/// overestimate and steps clean PAST the blade into the sky behind it — eroding the silhouette. WITH the clamp
/// (<c>SdfProgram.AnalyzeLipschitz</c> bakes <c>1/√2 ≈ 0.707</c> into the segment-header step scale — the SAME factor
/// <see cref="SdfLipschitzStage"/> pins on the CPU — and mapCore multiplies its final distance by it) the march stays
/// conservative and the blades render solid. This stage is the GPU consequence the CPU pin lacked.
/// <para>The test shape is a floating row of thin, bright EMISSIVE blades (tiled by <see cref="SdfOp.Repeat"/>, an
/// isometric fold — no clamp of its own): each cell is two thin-in-Z slabs crossed ~35° in the XY plane and
/// <see cref="SdfBlendOp.ChamferUnion"/>'d, so their four acute inner corners are chamfer bevels at the √2 gradient.
/// A low oblique camera looks down the row so grazing rays travel along the thin faces across many blades. No ground
/// plane, so every gap is unambiguous sky.</para>
/// <para>The detector mirrors <see cref="WorldWarpSolidityStage"/>/<see cref="WorldDisplaceSolidityStage"/>: a coverage
/// floor (silhouette-agnostic — the crossed-blade outline is irregular) is the primary tooth, with a loose enclosed-hole
/// net as a secondary catch. A bright EMISSIVE blade keeps every blade pixel high-red even where a crease self-shadows.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldChamferSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const int VacuityFloor = 20000;
    private const uint Width = 960;
    // The PRIMARY tooth: coverage below this means the silhouette eroded — the chamfer bevel overstepped as grazing rays
    // approached an acute weld. A healthy render measures 274,081 blade pixels with 3,000
    // enclosed (1.095%). The floor sits at ~0.8x healthy so a real erosion class (the unclamped field erodes blades
    // roughly in half, per the liar's-spiral precedent) fails decisively while thermal/framing noise cannot.
    private const int MinBladeCoverage = 219000;
    // SECONDARY net (deliberately loose, top of the neighbours' 0.006–0.03 band): interior sky the border flood-fill
    // can't reach. The crossed-blade creases leave a small enclosed-gap floor even when solid; this only trips on GROSS
    // holing. Coverage is the primary regression signal.
    // Healthy measures 1.095% enclosed; 3% keeps ~2.7x headroom — inside the neighbors' calibrated 0.006-0.03 band.
    private const double MaxEnclosedHoleFraction = 0.03;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red). 40 (14 above the sky ceiling)
    // re-counts shading-darkened blade edges/creases as blade on this HIGH-perimeter thin-blade row while a real march
    // hole (pure sky, red ~15-26) stays below it — the same threshold WorldDisplaceSolidityStage settled on for a
    // thin-blade row after the shading wave landed. VERIFY during calibration that the sky/blade valley is clean here.
    private const byte SolidRedThreshold = 40;

    /// <inheritdoc/>
    public string Name => "world-chamfer-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>A floating row of thin, bright EMISSIVE blades tiled by <see cref="SdfOp.Repeat"/> (spacing 0.9 in X,
    /// 6.0 in Y/Z so only the y=z=0 row is populated). Each cell is two thin-in-Z slabs — the second rotated ~35° about
    /// Z, an acute crossing — ChamferUnion'd (bevel 0.16), so the four acute inner corners are chamfer bevels at the √2
    /// gradient. No ground plane, so every gap is unambiguous sky. INTERNAL so <see cref="SdfLipschitzStage"/> could pin
    /// this exact program's baked step scale (≈ 1/√2) if desired.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildChamferedBladesScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.95f, y: 0.55f, z: 0.1f), Emissive: 0.7f));

        return builder
            .Repeat(spacing: new Vector3(x: 0.9f, y: 6.0f, z: 6.0f))
            .Box(halfExtents: new Vector3(x: 0.33f, y: 0.33f, z: 0.07f), round: 0f, material: brass)
            .Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (35f * (MathF.PI / 180f))))
            .Box(halfExtents: new Vector3(x: 0.33f, y: 0.33f, z: 0.07f), round: 0f, material: brass, blend: SdfBlendOp.ChamferUnion, smooth: 0.16f)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildChamferedBladesScene();
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-chamfer-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the chamfered blade row rendered only {solidPixels} blade pixels (< {VacuityFloor}) — the render is broken or the blades are unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Primary teeth: the chamfer bevel's √2 overestimate erodes coverage as grazing rays step past an acute weld.
        if (solidPixels < MinBladeCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the chamfered blade row eroded to {solidPixels} blade pixels (< {MinBladeCoverage}) — the ChamferUnion bevel's √2 gradient oversteps at the acute welds WITHOUT the Lipschitz step clamp");
        }

        // Secondary catch (silhouette-agnostic): sky reachable from any border is legitimate background; sky the
        // flood-fill cannot reach is walled in by blade — an overstep hole punched clean through the thin face.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the chamfered blade row holed: {enclosedHoles} interior sky pixels enclosed by blade ({(enclosedFraction * 100.0):0.##}% of {solidPixels} blade pixels) — the ChamferUnion bevel's √2 gradient oversteps WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the ChamferUnion-welded blade row (crossed thin slabs, bevel 0.16, stepScale {program.StepScale:0.###} ≈ 1/√2) renders SOLID on the Vulkan host: {solidPixels} blade pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the chamfer √2 step clamp holds the field 1-Lipschitz-safe");
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
        // A low, elevated oblique camera looking down the blade row: its rays graze across many chamfered faces at once
        // — exactly the condition that makes the non-1-Lipschitz bevel overstep. A face-on camera into a single blade
        // would never graze and never erode, hiding the very defect this stage guards.
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
