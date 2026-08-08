using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Cameras;
using Puck.Capture;
using Puck.Compositing;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// The fuzz HUNT's CHILD render node, hosted instead of the battery when the POST is launched with
/// <c>--hunt-render --hunt-seed N</c> by the <see cref="DriftHunt"/> orchestrator. ONE candidate per process — this is
/// the MANDATORY process-pair isolation the differential fuzzer already relies on (a malformed program can TDR the GPU,
/// and a shared process re-uses one DXC context that would corrupt the comparison), so the hunt spawns a fresh child
/// per seed exactly as <c>tools fuzz</c> does per gate seed.
/// <para>On its first frame — once the offscreen host has brought the device up — it generates the candidate program
/// from the seed (<see cref="DriftHuntProgram"/>), renders it on BOTH backends in-process through the shared
/// <see cref="WorldStage"/> render path (Vulkan/SPIR-V reference, the shared Tier-C Direct3D 12/DXIL comparand),
/// computes the <see cref="ParityMetrics"/> and the composite <see cref="DriftScore"/>, and prints ONE structured
/// <c>HUNT seed=… score=… …</c> line to stdout the parent parses. When launched with <c>--hunt-artifacts DIR</c>
/// (the champion re-run), it additionally writes the two backend renders, an amplified diff heatmap
/// (<see cref="ParityCheck.WriteDiffImage"/>, ×64 so ±1 LSB is visibly grey — the factor is stated in the filename),
/// and a one-command repro recipe JSON. It never presents (returns a default surface), so the window just flashes; the
/// verdict is the printed line plus the process exit code.</para>
/// </summary>
internal sealed class DriftHuntRenderNode : IRenderNode {
    /// <summary>The <c>--hunt-render</c> mode selector value.</summary>
    public const string ModeName = "hunt-render";
    // The amplification the diff heatmap uses (ParityCheck.WriteDiffImage: value = min(255, delta * 64)) — stated in
    // the champion diff filename so the factor is legible from the artifact alone.
    private const int DiffAmplification = 64;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    private readonly string? m_artifactsDirectory;
    private readonly NodeDescriptor m_descriptor = new(Name: "drift-hunt-render", SurfaceId: SurfaceId.New());
    private readonly int m_rank;
    private readonly PostRunResult m_runResult;
    private readonly int m_seed;
    private readonly IServiceProvider m_services;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="DriftHuntRenderNode"/> class.</summary>
    /// <param name="services">The application service provider (the compute services + the host device).</param>
    /// <param name="runResult">The shared carrier the child's exit code is written to.</param>
    /// <param name="seed">The candidate seed to render and score.</param>
    /// <param name="artifactsDirectory">The champion-artifact directory, or <see langword="null"/> for a score-only run.</param>
    /// <param name="rank">The champion rank (0-based), used only to name the artifacts on a write run.</param>
    public DriftHuntRenderNode(IServiceProvider services, PostRunResult runResult, int seed, string? artifactsDirectory, int rank) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runResult);

        m_artifactsDirectory = artifactsDirectory;
        m_rank = rank;
        m_runResult = runResult;
        m_seed = seed;
        m_services = services;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.Out.WriteLine(value: $"HUNT seed={m_seed} skip=cross-backend-tier-requires-windows-10.0.10240");
            m_runResult.ExitCode = 0;
        } else {
            _ = context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice);

            RenderAndReport(gpuDevice: gpuDevice);
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    // The hunt frame: one full-region viewport, pulled back and elevated on +Z (fov 58°) so the generator's
    // origin-clustered content plus the ground fills the frame. Time 0, no dynamic entities.
    private static SdfFrame BuildHuntFrame(SdfProgram program) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0.5f, y: 3.4f, z: 9f),
            target: new Vector3(x: 0f, y: 0.9f, z: 0f),
            fieldOfViewRadians: (58f * (MathF.PI / 180f)),
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
    private void RenderAndReport(IGpuDeviceContext? gpuDevice) {
        try {
            // A PostContext gives access to the shared Tier-C Direct3D 12 device through the same seam the battery uses.
            // Its ArtifactsDirectory is only a fallback here (this node writes its own named champion artifacts).
            using var postContext = new PostContext(services: m_services, gpuDevice: gpuDevice, artifactsDirectory: (m_artifactsDirectory ?? Path.Combine(path1: "artifacts", path2: "drift-hunt")));
            var program = DriftHuntProgram.Generate(seed: m_seed);
            var frame = BuildHuntFrame(program: program);

            var vulkanPixels = WorldStage.RenderWorldFrame(device: postContext.RequireGpuDevice(), gpu: postContext.Resolve<IGpuComputeServices>(), bytecodeExtension: ".spv", frame: frame, width: WorldWidth, height: WorldHeight);
            var directX = postContext.RequireDirectXDevice();
            var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => WorldStage.RenderWorldFrame(device: directX.DeviceContext, gpu: directX.Services.GetRequiredService<IGpuComputeServices>(), bytecodeExtension: ".dxil", frame: frame, width: WorldWidth, height: WorldHeight));

            var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)WorldWidth, height: (int)WorldHeight);
            var score = DriftScore.Compute(metrics: metrics);

            // The ONE structured line the parent parses (space-separated key=value pairs).
            Console.Out.WriteLine(value: $"HUNT seed={m_seed} score={score:0.####} diff={metrics.PercentDiffering:0.####} maxDelta={metrics.MaxChannelDelta} mean={metrics.MeanAbsError:0.######} isolated={metrics.IsolatedFraction:0.####} unit={metrics.UnitDeltaFraction:0.####} cluster={DriftScore.ClusterPercent(metrics: metrics):0.####}");

            if (m_artifactsDirectory is not null) {
                WriteChampionArtifacts(directory: m_artifactsDirectory, vulkanPixels: vulkanPixels, directXPixels: directXPixels, metrics: metrics, score: score);
            }

            m_runResult.ExitCode = 0;
        } catch (Exception exception) {
            Console.Out.WriteLine(value: $"HUNT seed={m_seed} error={exception.Message.Replace(oldChar: '\n', newChar: ' ').Replace(oldChar: '\r', newChar: ' ')}");
            m_runResult.ExitCode = 2;
        }
    }
    private void WriteChampionArtifacts(string directory, byte[] vulkanPixels, byte[] directXPixels, ParityMetrics metrics, double score) {
        _ = Directory.CreateDirectory(path: directory);

        var stem = $"champion-{m_rank:00}-seed{m_seed}";

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: directory, path2: $"{stem}-vulkan.png"), rgba: vulkanPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: directory, path2: $"{stem}-directx.png"), rgba: directXPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)WorldHeight, path: Path.Combine(path1: directory, path2: $"{stem}-diff-amp{DiffAmplification}x.png"), reference: vulkanPixels, width: (int)WorldWidth);

        // The one-command repro: the seed reproduces the program deterministically. The exotic operations are not
        // authorable from a run document, so the repro uses a seed recipe. Hand-written
        // JSON to avoid pulling a serializer into a leaf node.
        var reproCommand = $"dotnet run --project src/Puck.Post -c Release -- --hunt-render --hunt-seed {m_seed} --hunt-artifacts artifacts/drift-hunt --hunt-rank {m_rank}";
        var json =
            ((((((($"{{\n" +
            $"  \"kind\": \"puck.drift-hunt.champion.v1\",\n") +
            $"  \"rank\": {m_rank},\n") +
            $"  \"seed\": {m_seed},\n") +
            $"  \"score\": {score.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)},\n") +
            $"  \"metrics\": {{ \"percentDiffering\": {metrics.PercentDiffering.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}, \"maxChannelDelta\": {metrics.MaxChannelDelta}, \"meanAbsError\": {metrics.MeanAbsError.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}, \"isolatedFraction\": {metrics.IsolatedFraction.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}, \"unitDeltaFraction\": {metrics.UnitDeltaFraction.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}, \"clusterPercent\": {DriftScore.ClusterPercent(metrics: metrics).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} }},\n") +
            $"  \"repro\": \"{reproCommand}\"\n") +
            $"}}\n");

        File.WriteAllText(path: Path.Combine(path1: directory, path2: $"{stem}-repro.json"), contents: json);
    }
}
