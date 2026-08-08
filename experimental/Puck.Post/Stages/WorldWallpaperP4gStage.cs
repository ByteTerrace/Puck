using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Capture;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C absolute-correctness proof for P4G single-cell translation invariance. Cross-backend parity alone cannot
/// prove the wallpaper-group identity because two backends can agree on the same incorrect fold.
/// <para>p4g's translation lattice period is exactly one fold <c>cell</c> — a pair of opposed 4-fold centers (the cell
/// center and a cell corner) compose to the unit translation. The stage renders the same scene unshifted and translated
/// by exactly one cell on the same backend; the images must agree within cell-boundary floating-point noise.</para>
/// <para>The motif — a leaning round-cone plus an off-center sphere — is CHIRAL and asymmetric (no rotational or mirror
/// self-symmetry), and it sits inside the fundamental wedge clear of the fold's
/// quadrant seams. The lattice <c>limit</c> is large and the camera frames only interior cells, so the RepeatLimited
/// clamp edge never enters view (a clamped edge would move under the shift and mask the real test). Deterministic: a
/// fixed scene, camera, and single frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldWallpaperP4gStage : IPostStage {
    private const float CellSize = 0.8f;
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    // The base render must contain enough rose motif pixels to make the invariance comparison non-vacuous. This floor is
    // deliberately far below the normal framed population and detects broken or empty renders rather than image quality.
    private const int MotifVacuityFloor = 1500;
    private const uint Width = 960;

    /// <inheritdoc/>
    public string Name => "world-wallpaper-p4g";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    /// <summary>Builds the p4g invariance scene: a ground plane plus a single P4G-folded chiral motif over a large-limit
    /// lattice, the whole fold input pre-shifted by <paramref name="cellShift"/>. Shifting by exactly one cell along the
    /// lattice axis tests p4g's period-1 translation invariance; the unshifted and one-cell-shifted programs must render
    /// identically for a correct p4g fold.</summary>
    /// <param name="cellShift">The pre-fold translation applied to the lattice, in world units (0 = base, one cell = the shift).</param>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildP4gInvarianceScene(Vector3 cellShift) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.45f, y: 0.5f, z: 0.55f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.35f, z: 0.45f)));

        // A high, generous cell limit so the RepeatLimited clamp edge is far outside the framed interior in BOTH the
        // base and the shifted program — the visible region is a pure infinite p4g lattice, where translation invariance
        // is exact up to cell-boundary FP noise.
        var limit = new Vector2(x: 16f, y: 16f);

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // Chain 1: the leaning cone. cellShift moves the whole lattice by one cell for the shifted program.
            // materialStride MUST be 0 here: the cell-key material stride legitimately recolors under any lattice
            // shift (the key changes with the cell index), which would swamp the geometric invariance this stage
            // exists to test — stride-free, GEOMETRY is the only variable between the two renders.
            .Translate(offset: cellShift)
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(x: CellSize, y: CellSize), limit: limit, materialStride: 0)
            // Placed at ~(0.13, 0.13) in the fold plane — inside the fundamental wedge {x>=0, z>=0, x+z <= cell/2},
            // clear of the quadrant seams (x=0, z=0) and the offset-diagonal mirror. The lean + the sphere below make
            // the motif chiral, so a quarter-turn visibly relocates it.
            .Translate(offset: new Vector3(x: 0.13f, y: 0.16f, z: 0.13f))
            .RoundCone(lowerRadius: 0.1f, upperRadius: 0.03f, height: 0.24f, material: rose)
            .ResetPoint()
            .Translate(offset: cellShift)
            .WallpaperFold(group: SdfWallpaperGroup.P4G, cell: new Vector2(x: CellSize, y: CellSize), limit: limit, materialStride: 0)
            .Translate(offset: new Vector3(x: 0.07f, y: 0.04f, z: 0.18f))
            .Sphere(radius: 0.06f, material: rose)
            .Build();
    }

    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();

        var baseProgram = BuildP4gInvarianceScene(cellShift: Vector3.Zero);
        // One cell along the lattice X axis (fold-plane axis A) must leave period-1 P4G geometry unchanged.
        var shiftedProgram = BuildP4gInvarianceScene(cellShift: new Vector3(x: CellSize, y: 0f, z: 0f));

        var basePixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: baseProgram), width: Width, height: Height);
        var shiftedPixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: shiftedProgram), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var basePath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-wallpaper-p4g-base.png");

        PngEncoder.Write(height: (int)Height, path: basePath, rgba: basePixels, width: (int)Width);

        var motifPixels = CountMotif(pixels: basePixels);

        if (motifPixels < MotifVacuityFloor) {
            return PostStageOutcome.Infra(detail: $"the p4g lattice rendered only {motifPixels} motif pixels (< {MotifVacuityFloor}) — the render is broken or the lattice is unframed, so translation invariance cannot be judged (artifact: {basePath})");
        }

        // Same backend and camera: the shifted program must match the base within the WorldComposite benign-noise band.
        return ParityCheck.WriteEvaluateReport(
            artifactsDirectory: context.ArtifactsDirectory,
            prefix: "world-wallpaper-p4g",
            referencePixels: basePixels,
            comparandPixels: shiftedPixels,
            width: (int)Width,
            height: (int)Height,
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{Width}x{Height} P4G period-1 single-cell translation invariance | base vs one-cell-shifted lattice on the Vulkan host, within WorldComposite benign-noise thresholds ({motifPixels} motif pixels framed)"
        );
    }

    // A rose motif pixel: the rose albedo (0.9, 0.35, 0.45) leaves the red channel well above green, while the grey
    // ground (r≈g) and the blue sky (r<g) do not — so red minus green isolates the motif footprint.
    private static int CountMotif(byte[] pixels) {
        var count = 0;

        for (var i = 0; (i < ((int)Width * (int)Height)); i++) {
            var red = pixels[(i * 4)];
            var green = pixels[((i * 4) + 1)];

            if (red > (green + 40)) {
                count++;
            }
        }

        return count;
    }
    private static SdfFrame BuildFrame(SdfProgram program) {
        // An elevated oblique camera over the lattice origin, framing a block of interior cells (all far from the ±16
        // limit clamp). High enough that several cells tile in view so a quarter-turn of the whole block is unmistakable.
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0.5f, y: 3.4f, z: 4.2f),
            target: new Vector3(x: 0f, y: 0.2f, z: 0f),
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
