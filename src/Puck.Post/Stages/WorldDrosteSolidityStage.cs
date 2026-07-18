using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C correctness check for the fold-safe step bound. It renders a Droste scene from inside the fold and asserts
/// that the result is free of tile-sized holes. The tunnel solidity stage
/// (<see cref="WorldLogSphereSolidityStage"/>) complements this test with a camera outside the fold.
/// <para>The mechanism this guards: a folded field measures only the NEAREST shell's copy, so its VALUE OVERESTIMATES
/// true distance near a shell boundary (with twist, the neighbor shell is a rotated copy — the containment ≠
/// nearest-copy class). The beam's cone-clearance proof trusted that value and classified 16-px tiles straight
/// through shell geometry: tile-granular black holes checkerboarding shells and sky, shared by every downstream
/// march because they inherit the tile's marchStart. The fold-safe step bound
/// (<c>sdfMapStepBound</c>): marchers step — and build clearance proofs — with min(value, distance-to-fold-boundary)
/// while terminating on the raw value.</para>
/// <para>Camera IN-FIELD with twist — every ray crosses many shell boundaries at varied angles, the exact condition
/// that shattered. Teeth mirror the tunnel stage: a coverage floor (shatter erodes lit shell pixels) and an
/// enclosed-hole fraction (a tile hole in a shell face is sky fully walled in by shell — the border flood-fill
/// cannot reach it). A bright emissive material keeps every shell pixel high-red under any shading.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldDrosteSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (55f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host with a deterministic fixed scene and camera, measuring both sides by
    // stash-toggling the fold-safe step bound): WITH the bound 553,931 shell pixels / 10,151 enclosed (1.833% — the
    // residual pixel-level containment-crease speckle); WITHOUT it 472,266 / 79,922 enclosed (16.92% — the tile
    // checkerboard). The ENCLOSED-HOLE fraction is the primary tooth for this scene (9.2x separation); coverage only
    // erodes ~15% under the defect, so its floor just catches catastrophic breakage. A render far below even the
    // eroded count is INFRA (broken/unframed), not a hole.
    private const int VacuityFloor = 40000;
    // Catastrophic-erosion catch only — both measured sides sit far above this; the enclosed fraction is the tooth.
    private const int MinShellCoverage = 300000;
    // THE primary tooth: 2.7x over the measured benign 1.833%, 3.4x under the defect's 16.92%.
    private const double MaxEnclosedHoleFraction = 0.05;
    // Sky tops out low in red; the warm emissive shells stay high-red under any shading. Same split as the tunnel.
    private const byte SolidRedThreshold = 90;

    /// <inheritdoc/>
    public string Name => "world-droste-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>The breakdown scene: the repro torus tiled by a SPINNING log-spherical fold (twist makes every shell
    /// boundary laterally discontinuous — the worst case), no ground plane, viewed from INSIDE the field so every
    /// ray crosses many boundaries. Matches the bisect repro (docs/sdf-backlog.md item 24's evidence).</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildDrosteBreakdownScene() {
        var builder = new SdfProgramBuilder();
        var ember = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.95f, y: 0.45f, z: 0.2f), Emissive: 0.7f));

        return builder
            .LogSphere(shellRatio: 2.0f, twist: 0.6f)
            .Torus(majorRadius: 0.7f, minorRadius: 0.16f, material: ember)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildDrosteBreakdownScene();
        var frame = BuildFrame(program: program);
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: frame, width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-droste-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the in-field Droste view rendered only {solidPixels} shell pixels (< {VacuityFloor}) — the render is broken or unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Catastrophic-erosion catch (the enclosed fraction below is the primary tooth for this scene).
        if (solidPixels < MinShellCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the in-field Droste view eroded to {solidPixels} shell pixels (< {MinShellCoverage}) — the beam's cone proof strode across shell boundaries the folded field's value lies about (the tile-shatter defect; the fold-safe step bound is not holding)");
        }

        // THE primary tooth: interior sky pockets fully walled in by shell — the checkerboard's signature.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the in-field Droste view holed: {enclosedHoles} interior sky pixels enclosed by shell ({(enclosedFraction * 100.0):0.##}% of {solidPixels} shell pixels, > {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed) — tile-granular breakdown (the fold-safe step bound is not holding)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the in-field twisted Droste view (shellRatio 2.0, twist 0.6) renders SOLID on the Vulkan host: {solidPixels} shell pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the fold-safe step bound keeps beam tile classification and every march honest across shell boundaries");
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

    // Flood-fill the NON-solid (sky) pixels inward from every border pixel (4-connected, iterative), then count the
    // non-solid pixels the fill never reached: sky pockets fully walled in by shell — tile-shatter holes.
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
        // The bisect repro's pose: INSIDE the fold, slightly above the prototype's plane, looking through nested
        // shells — every ray crosses many boundaries at varied angles. An outside camera (the tunnel stage's) crosses
        // few boundaries and never shattered; this pose is the one that did.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 1.117f, y: 0.299f, z: 1.632f),
            target: new Vector3(x: 0f, y: 0f, z: 0f),
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
