using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the D1 Lipschitz CORRECTNESS proof, and the one gate cross-backend PARITY cannot provide: both
/// backends overstep a non-1-Lipschitz field IDENTICALLY, so a parity diff of two equally-wrong renders passes while
/// both are wrong. This stage instead renders a shape KNOWN to be solid on ONE backend (the Vulkan host) and asserts
/// its interior has no punched-through holes.
/// <para>The test shape is "the liar's spiral": a WIDE, THIN box blade twisted about Y at rate 3.0 (the validator's
/// warp ceiling). A thin blade is the point — a chunky convex column can't hole in its interior (an overstep just
/// lands deeper inside, still a hit), but a thin blade's field, sphere-traced WITHOUT the Lipschitz step clamp,
/// OVERESTIMATES true distance by the twist's operator norm and steps clean THROUGH the ~0.16-thick blade into the
/// sky behind it — punching interior holes into a solid face. WITH the clamp (SdfProgram.AnalyzeLipschitz bakes 1/L
/// into the segment-header step scale; mapCore multiplies its final distance by it), the steps stay conservative and
/// the blade renders solid.</para>
/// <para>The detector is silhouette-AGNOSTIC (a twisted blade's outline is wavy, so an axis-aligned bbox is useless):
/// flood-fill the background inward from the image borders, then any background (sky) pixel the fill cannot reach is
/// ENCLOSED by blade — a genuine overstep hole, never a concavity of the outline. With the clamp that count is ~0;
/// without it the holes are percent-scale. A bright EMISSIVE blade keeps every blade pixel high-red even where a face
/// turns from the light, so a self-shadowed interior can never be mistaken for a hole.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldWarpSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host (deterministic, fixed scene/camera): the clamp renders 50923 blade pixels; WITHOUT
    // it the rate-3.0 twist oversteps at the blade's outer edges and erodes the silhouette to 28157 (a 45% collapse).
    // The two coverage constants sit in that gap so a clamp regression FAILS loudly and a benign edge-pixel shift does
    // not. A truly broken / unframed / black render (far below even the eroded 28157) is INFRA, not a holing FAIL.
    private const int VacuityFloor = 8000;
    // The primary teeth: coverage this low means the silhouette eroded — the field overstepped. 42000 clears the
    // clamped 50923 by ~17% (survives later D1 increments' benign edge shifts) yet sits far above the unclamped 28157.
    private const int MinBladeCoverage = 42000;
    // Secondary catch: interior sky pixels fully walled in by blade (overstep holes that DON'T reach the silhouette).
    // The clamp leaves ~0.24%; this covers the wavy blade's self-occluding folds without masking a real interior hole.
    private const double MaxEnclosedHoleFraction = 0.006;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red); the warm emissive blade is
    // always HIGH red (>= ~150/255 from emissive alone, under any shading). The threshold sits far from both.
    private const byte SolidRedThreshold = 90;

    /// <inheritdoc/>
    public string Name => "world-warp-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>The liar's spiral: a WIDE, THIN box blade twisted about Y at rate 3.0. Thin enough that an unclamped
    /// overstep tunnels through it; wide enough that the holes land in a broad interior the border flood-fill can't
    /// reach. A bright warm EMISSIVE material makes the blade unmistakably high-red under any shading. INTERNAL so the
    /// sdf-lipschitz stage can assert this exact program's baked step scale is clamped (&lt; 1).</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildLiarsSpiralScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.55f, 0.1f), Emissive: 0.7f));

        return builder
            .TwistY(rate: 3.0f)
            .Box(halfExtents: new Vector3(1.6f, 1.2f, 0.08f), round: 0.02f, material: brass)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildLiarsSpiralScene();
        var frame = BuildFrame(program: program);
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: frame, width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "world-warp-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the liar's spiral rendered only {solidPixels} blade pixels (< {VacuityFloor}) — the render is broken or the blade is unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Primary teeth: the twist oversteps at the outer edges, so a missing clamp ERODES the silhouette (coverage
        // collapses) rather than punching enclosed interior holes — the coverage floor is the loud, unambiguous signal.
        if (solidPixels < MinBladeCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the liar's spiral eroded to {solidPixels} blade pixels (< {MinBladeCoverage}; the clamp renders ~58630) — the rate-3.0 twist oversteps and eats the silhouette WITHOUT the Lipschitz step clamp");
        }

        // Secondary catch (silhouette-agnostic): sky reachable from any border is legitimate background; sky the
        // flood-fill cannot reach is walled in by blade — an overstep hole punched clean through the thin face.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the liar's spiral holed: {enclosedHoles} interior sky pixels enclosed by blade ({(enclosedFraction * 100.0):0.##}% of {solidPixels} blade pixels) — the rate-3.0 twist oversteps WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the liar's spiral (twist rate 3.0) renders SOLID on the Vulkan host: {solidPixels} blade pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the D1 Lipschitz step clamp holds the field 1-Lipschitz-safe (unclamped this erodes to ~28157)");
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
        // An ELEVATED camera looking down the blade: its rays descend in Y as they travel, so the Y-keyed twist angle
        // changes along each ray — exactly the condition that makes a non-1-Lipschitz twist overstep. A face-on camera
        // (constant Y per ray) would see a constant rotation and never hole, hiding the very defect this stage guards.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(1.7f, 3.2f, 6.4f),
            target: new Vector3(0f, 0.1f, 0f),
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
