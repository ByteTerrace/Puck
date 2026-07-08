using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the D2 log-spherical CORRECTNESS proof, and the gate cross-backend PARITY cannot provide: a
/// non-1-Lipschitz field oversteps IDENTICALLY on both backends, so a parity diff of two equally-holed renders passes
/// while both are wrong. This stage instead renders a log-spherical scene KNOWN to be solid on ONE backend (the Vulkan
/// host) and asserts its shells have no tunnelled-through holes.
/// <para>The test shape is a Droste tunnel of nested rings: a THIN emissive torus tiled by <see cref="SdfOp.LogSphere"/>
/// (shellRatio 2.0) into self-similar coaxial rings with sky between them. The log-spherical fold's SHELL-BOUNDARY
/// discontinuities (the nearest-shell <c>round</c> jump) make its field OVERESTIMATE true distance across a boundary;
/// sphere-traced OVER-RELAXED (omega 1.2) WITHOUT the D1 Lipschitz step clamp, an inward ray oversteps the thin ring
/// wall at a boundary and tunnels into the sky behind it — eroding coverage and punching interior holes. WITH the clamp
/// (SdfProgram.AnalyzeLipschitz bakes <c>1/exp(w/2)</c> into the segment-header step scale; mapCore
/// multiplies its final distance by it), the over-relaxed steps stay conservative across every shell boundary and the
/// rings render solid.</para>
/// <para>Two teeth, both silhouette-AGNOSTIC (the nested rings' outline is a stack of arcs, so a bbox is useless): a
/// COVERAGE floor (an overstepping field erodes the bright ring pixels), and an ENCLOSED-HOLE fraction — flood-fill the
/// background inward from the image borders, then any sky pixel the fill cannot reach is walled in by ring, a genuine
/// tunnel hole (the rings' true gaps stay open to the border, so they are not false positives). A bright EMISSIVE torus
/// keeps every ring pixel high-red under any shading, so a self-shadowed fold can never read as a hole.</para>
/// <para>Deterministic: a fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldLogSphereSolidityStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // Calibrated on the Vulkan host (deterministic, fixed scene/camera): WITH the clamp the Droste rings render
    // <clamped> bright pixels; WITHOUT it the shellRatio-2.0 fold oversteps at every shell boundary and erodes/holes
    // them to <eroded>. The coverage constants sit in that gap so a clamp regression FAILS loudly and a benign edge
    // shift does not. A truly broken / unframed / black render (far below even the eroded count) is INFRA, not a hole.
    private const int VacuityFloor = 6000;
    // The primary tooth: coverage this low means the rings eroded — the field overstepped.
    private const int MinShellCoverage = 24000;
    // Secondary catch: interior sky pixels fully walled in by ring (overstep holes that DON'T reach the silhouette).
    private const double MaxEnclosedHoleFraction = 0.01;
    // A background (sky) pixel has a LOW red channel (skyColor tops out ~26/255 in red); the warm emissive ring is
    // always HIGH red (>= ~150/255 from emissive alone, under any shading). The threshold sits far from both.
    private const byte SolidRedThreshold = 90;

    /// <inheritdoc/>
    public string Name => "world-log-sphere-solidity";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>The Droste tunnel: a THIN, bright EMISSIVE torus tiled by a log-spherical fold (shellRatio 2.0, no spin)
    /// into self-similar coaxial rings with sky between them. Thin enough that an unclamped overstep tunnels through a
    /// ring wall; the nested rings leave broad sky gaps a border flood-fill reaches, so a real tunnel leaves an ENCLOSED
    /// pocket. No ground plane, so every gap is unambiguous sky. INTERNAL so the sdf-lipschitz stage can assert this
    /// exact program's baked step scale is clamped (0 &lt; s &lt; 1, ≈ 1/exp(w/2)).</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildDrosteTunnelScene() {
        var builder = new SdfProgramBuilder();
        var brass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.95f, 0.55f, 0.1f), Emissive: 0.7f));

        return builder
            .LogSphere(shellRatio: 2.0f, twist: 0.9f)
            .Translate(offset: new Vector3(1.0f, 0f, 0f))
            .Sphere(radius: 0.5f, material: brass)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildDrosteTunnelScene();
        var frame = BuildFrame(program: program);
        var pixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: frame, width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "world-log-sphere-solidity.png");

        PngEncoder.Write(height: (int)Height, path: artifactPath, rgba: pixels, width: (int)Width);

        var solidPixels = CountSolid(pixels: pixels);

        if (solidPixels < VacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the Droste tunnel rendered only {solidPixels} ring pixels (< {VacuityFloor}) — the render is broken or the rings are unframed, so solidity cannot be judged (artifact: {artifactPath})");
        }

        // Primary tooth: the fold oversteps at the shell boundaries, so a missing clamp ERODES the bright ring pixels.
        if (solidPixels < MinShellCoverage) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the Droste tunnel eroded to {solidPixels} ring pixels (< {MinShellCoverage}) — the shellRatio-2.0 log-spherical fold oversteps at its shell boundaries WITHOUT the Lipschitz step clamp");
        }

        // Secondary catch (silhouette-agnostic): sky reachable from any border is legitimate background; sky the
        // flood-fill cannot reach is walled in by ring — an overstep hole punched clean through a thin ring wall.
        var enclosedHoles = CountEnclosedHoles(pixels: pixels);
        var enclosedFraction = ((double)enclosedHoles / solidPixels);

        if (enclosedFraction > MaxEnclosedHoleFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the Droste tunnel holed: {enclosedHoles} interior sky pixels enclosed by ring ({(enclosedFraction * 100.0):0.##}% of {solidPixels} ring pixels) — the log-spherical fold oversteps at its shell boundaries WITHOUT the Lipschitz step clamp (> {(MaxEnclosedHoleFraction * 100.0):0.##}% allowed)");
        }

        return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"the log-spherical Droste tunnel (shellRatio 2.0) renders SOLID on the Vulkan host: {solidPixels} ring pixels, {enclosedHoles} enclosed ({(enclosedFraction * 100.0):0.###}%) — the D1 Lipschitz step clamp (1/exp(w/2)) holds the over-relaxed march conservative across every shell boundary");
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
    // sky pockets fully walled in by ring, i.e. overstep holes.
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
        // An ELEVATED, offset camera looking down at the coaxial rings' plane: its rays descend and cross several shell
        // boundaries at grazing angles — exactly the condition that makes the non-1-Lipschitz fold overstep. A face-on
        // camera (a ray staying within one shell) would never cross a boundary and never hole, hiding the defect.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(0.5f, 1.2f, 9.0f),
            target: new Vector3(0.5f, 0.4f, 0f),
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
