using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C7. Cross-backend DIFFERENTIAL FUZZING of the SDF VM over a FIXED, deterministic seed list — the
/// POST's fast sample of the demo's <c>--validate-world --fuzz-seed N</c> gate (whose overnight fuzzer ran 64
/// iterations; the POST wants deterministic, repeatable coverage in seconds). Each seed generates a
/// random-but-renderable scene program through the SHARED <see cref="Puck.Scene.FuzzSdfProgram"/> generator (the one
/// implementation the demo gate also consumes, so every POST seed reproduces in the demo gate), renders it through the
/// identical <see cref="Puck.SdfVm.SdfWorldEngine"/> on BOTH backends — the Vulkan host (SPIR-V) and the shared
/// LUID-matched Tier-C Direct3D 12 device (DXIL) — and diffs under the <c>WorldFuzz</c> thresholds: fuzz scenes span
/// the whole shape/blend parameter space, so benign ±1-LSB codegen noise legitimately clusters along gradients, and
/// the oracle keys on the benign signature (the delta mass exactly ±1, an isolated march-amplified few-LSB tail
/// bounded by the max-delta cap — see the <c>WorldFuzz</c> threshold doc) instead of the showcase isolation
/// guard. A failure names the offending seed, so the finding is immediately reproducible (here and in the demo).
/// Artifacts: the first seed's backend pair as smoke; a failing seed additionally writes its pair + diff heatmap.
/// </summary>
internal sealed class FuzzStage : IPostStage {
    // The camera is the t=0 pose of the demo fuzz gate's single hero orbit (DemoRunDocuments.SingleViewports:
    // azimuth 0, fov 60°, height 1.6, radius 5.2, target (0, 0.1, 0)) → eye (5.2, 1.7, 0) looking at the target.
    private static readonly Vector3 CameraPosition = new(5.2f, 1.7f, 0f);
    private static readonly Vector3 CameraTarget = new(0f, 0.1f, 0f);
    private const float FieldOfViewRadians = (60f * (MathF.PI / 180f));
    // The default fixed seed list: 7 is the demo cross-check seed (`--validate-world --fuzz-seed 7`); the rest
    // spread the deterministic generator across its branch space (shape mix, blend ops, the 30%/20% rotate/scale
    // rolls) — five seeds keep the battery fast while every one stays individually reproducible.
    private static readonly int[] DefaultSeeds = [1, 7, 23, 42, 91];
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    private readonly int[] m_seeds;

    /// <summary>Initializes a new instance of the <see cref="FuzzStage"/> class with the default fixed seed list.</summary>
    public FuzzStage() : this(seeds: DefaultSeeds) { }

    /// <summary>Initializes a new instance of the <see cref="FuzzStage"/> class with an overridden seed list — the
    /// <c>--fuzz-seed</c> CLI seam, for a single-seed sweep run outside the battery's default sample.</summary>
    /// <param name="seeds">The seed list to render and diff.</param>
    public FuzzStage(int[] seeds) {
        ArgumentNullException.ThrowIfNull(argument: seeds);

        m_seeds = seeds;
    }

    /// <inheritdoc/>
    public string Name => "fuzz";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Luma standard deviation of an RGBA frame — a cheap "is there signal" proxy, ported from the demo gate's
    // content-sanity check: identical-degenerate (flat/black) output passes the cross-backend diff, so a low value
    // marks the seed low-signal rather than counting it as meaningful coverage.
    private static double FrameCoverage(byte[] rgba) {
        if (rgba.Length < 4) {
            return 0.0;
        }

        var pixelCount = (rgba.Length / 4);
        var sum = 0.0;
        var sumOfSquares = 0.0;

        for (var pixel = 0; (pixel < pixelCount); pixel++) {
            var offset = (pixel * 4);
            var luma = ((0.299 * rgba[offset]) + (0.587 * rgba[offset + 1]) + (0.114 * rgba[offset + 2]));

            sum += luma;
            sumOfSquares += (luma * luma);
        }

        var mean = (sum / pixelCount);

        return Math.Sqrt(d: Math.Max(0.0, ((sumOfSquares / pixelCount) - (mean * mean))));
    }

    // The fixed frame every seed renders: a single full-region viewport over the generated program, time 0, no
    // dynamic entities — the fuzzer varies the SCENE, so everything else stays constant across seeds and backends.
    private static SdfFrame BuildFuzzFrame(SdfProgram program) {
        var camera = CameraSnapshot.LookAt(
            position: CameraPosition,
            target: CameraTarget,
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: WorldWidth,
            viewportHeight: WorldHeight
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private PostStageOutcome RunCore(PostContext context) {
        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var directX = context.RequireDirectXDevice();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var degenerateSeeds = new List<int>();
        var worstDescription = string.Empty;
        var worstPercent = -1.0;

        foreach (var seed in m_seeds) {
            var program = FuzzSdfProgram.Generate(bounds: ShapeBounds.Default, seed: seed);
            var frame = BuildFuzzFrame(program: program);

            // Vulkan reference: the host device + the host's neutral compute services, SPIR-V kernels.
            byte[] vulkanPixels;

            using (var vulkanRenderer = new SdfWorldEngine(
                device: vulkanDevice,
                gpu: vulkanGpu,
                height: WorldHeight,
                kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
                options: new SdfWorldEngineOptions(Program: program),
                width: WorldWidth
            )) {
                vulkanPixels = vulkanRenderer.RenderFrame(frame: frame);
            }

            // Direct3D 12 comparand: the SHARED Tier-C device + its neutral compute services, DXIL kernels — the
            // identical engine and the identical generated program, only the backend differs.
            var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => {
                using var directXRenderer = new SdfWorldEngine(
                    device: directX.DeviceContext,
                    gpu: directXGpu,
                    height: WorldHeight,
                    kernels: SdfWorldKernels.Load(bytecodeExtension: ".dxil"),
                    options: new SdfWorldEngineOptions(Program: program),
                    width: WorldWidth
                );

                return directXRenderer.RenderFrame(frame: frame);
            });

            // Smoke artifact: the first seed's backend pair is always written, so a green run still leaves evidence.
            if (seed == m_seeds[0]) {
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, $"fuzz-{seed}-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, $"fuzz-{seed}-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
            }

            var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
            var failures = ParityThresholds.WorldFuzz.Evaluate(metrics: metrics);

            if (failures.Count != 0) {
                // Fail NAMES the seed (reproducible here and via the demo's --validate-world --fuzz-seed) and writes
                // the full artifact triple for it.
                var diffPath = Path.Combine(context.ArtifactsDirectory, $"fuzz-{seed}-diff.png");

                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, $"fuzz-{seed}-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
                PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, $"fuzz-{seed}-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
                ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanPixels, width: (int)WorldWidth);

                return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"seed {seed} diverged | {ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
            }

            if (FrameCoverage(rgba: vulkanPixels) < 1.0) {
                degenerateSeeds.Add(item: seed);
            }

            if (metrics.PercentDiffering > worstPercent) {
                worstPercent = metrics.PercentDiffering;
                worstDescription = $"worst seed {seed}: {ParityCheck.Describe(metrics: metrics)}";
            }
        }

        // A single low-signal seed can be legitimate (the generator's parameter space includes sparse scenes), but if
        // EVERY seed renders degenerate the generator itself regressed to flat/empty output — and the cross-backend
        // diff passes trivially on identical-degenerate frames, so it would otherwise go green with only a note. Fail.
        if (degenerateSeeds.Count == m_seeds.Length) {
            return PostStageOutcome.Fail(detail: $"every fuzz seed rendered a degenerate (near-flat) scene — the generator regressed to empty output (seeds {string.Join(separator: ",", values: m_seeds)})");
        }

        var coverageNote = ((degenerateSeeds.Count == 0) ? string.Empty : $" | low-signal (degenerate) seeds: {string.Join(separator: ",", values: degenerateSeeds)}");

        return PostStageOutcome.Pass(detail: $"{m_seeds.Length} seeds ({string.Join(separator: ",", values: m_seeds)}) at {WorldWidth}x{WorldHeight} | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldFuzz thresholds | {worstDescription}{coverageNote}");
    }
}
