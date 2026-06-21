using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX.Apis;
using Puck.Hosting;
using Puck.Scene;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// A one-shot cross-backend parity gate (installed under <c>--validate-world</c>). It renders the SAME backend-
/// neutral <see cref="WorldProducerNode"/> twice at the same fixed (delta-zero) frame: once on the Vulkan host
/// device (SPIR-V kernels, the demo's Vulkan compute services) and once on a bespoke Direct3D 12 device (DXIL
/// kernels, the D3D12 compute services) LUID-matched to the same adapter — capturing each to a PNG. Because the
/// node is identical and the frame is fixed, the two images should match; it proves the neutral IGpuCompute* seam
/// runs the compute SDF world on EITHER backend. It then diffs the two readbacks tolerance-aware (the same
/// <see cref="ParityMetrics"/>/<see cref="ParityThresholds"/> the <c>--validate</c> graphics gate uses) and writes
/// a diff heatmap. 0 = pass, 1 = pixel-diff gate-fail, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class WorldParityNode : IRenderNode {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "world-parity",
        SurfaceId: SurfaceId.New()
    );
    private readonly string m_artifactDirectory;
    private readonly ShapeBounds m_bounds;
    private readonly string m_diffCapturePath;
    private readonly string m_directXCapturePath;
    private readonly Func<ISdfFrameSource>? m_frameSourceFactory;
    private readonly int? m_fuzzSeed;
    private SdfProgram? m_program;
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private readonly ParityThresholdSet? m_thresholds;
    private readonly string m_vulkanCapturePath;
    private readonly bool m_withChild;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="WorldParityNode"/> class.</summary>
    /// <param name="serviceProvider">The demo's service provider (the Vulkan-hosted compute services + the Vulkan device APIs for the LUID).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    /// <param name="withChild">Whether the bottom-right slot is a hosted <see cref="ChildSurfaceNode"/> instead of an SDF camera (captured to a distinct path); the injected factory then supplies the four-viewport split.</param>
    /// <param name="fuzzSeed">When set, render a fuzz-generated scene program (the same one on both backends) over the built-in single viewport — one differential-fuzzing iteration.</param>
    /// <param name="frameSourceFactory">When set, the source of the scene/cameras for BOTH backends (a fresh instance per backend) — the data-driven path injects the document's <c>JsonSdfFrameSource</c> here, so the gate diffs the DOCUMENT's scene across backends.</param>
    /// <param name="thresholds">When set, the PASS thresholds to apply instead of the calibrated default for the gate flavour.</param>
    /// <param name="artifactDir">The directory the captures + diff heatmap are written to; defaults to <c>artifacts</c>.</param>
    /// <param name="bounds">The fuzz generation envelope (used only with <paramref name="fuzzSeed"/>); defaults to <see cref="ShapeBounds.Default"/>.</param>
    public WorldParityNode(IServiceProvider serviceProvider, ParityResult result, bool withChild = false, int? fuzzSeed = null, Func<ISdfFrameSource>? frameSourceFactory = null, ParityThresholdSet? thresholds = null, string? artifactDir = null, ShapeBounds? bounds = null) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_bounds = (bounds ?? ShapeBounds.Default);
        m_frameSourceFactory = frameSourceFactory;
        m_fuzzSeed = fuzzSeed;
        m_result = result;
        m_serviceProvider = serviceProvider;
        m_thresholds = thresholds;
        m_withChild = withChild;

        var directory = (artifactDir ?? "artifacts");
        var prefix = (withChild ? "parity-world-child" : "parity-world");

        // The directory is created lazily in Validate() (inside ProduceFrame's try/catch) so an I/O failure becomes the
        // documented exit-2 infra-fail rather than an unhandled exception during DI activation.
        m_artifactDirectory = directory;
        m_diffCapturePath = Path.Combine(path1: directory, path2: $"{prefix}-diff.png");
        m_directXCapturePath = Path.Combine(path1: directory, path2: $"{prefix}-directx.png");
        m_vulkanCapturePath = Path.Combine(path1: directory, path2: $"{prefix}-vulkan.png");
    }

    // The scene/cameras for one backend: the injected data-driven factory (a fresh instance, so each backend gets a
    // pristine program-upload-pending source), else the fuzz path — the built-in single viewport over the generated
    // program (m_program is non-null whenever the factory is absent, i.e. a fuzz run).
    private ISdfFrameSource CreateFrameSource() {
        return (m_frameSourceFactory?.Invoke() ?? RunDocument.CreateFrameSource(program: m_program!, viewports: DemoRunDocuments.SingleViewports));
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

        // A fixed delta-zero frame so both backends place the cameras identically (the orbit cameras don't advance).
        var fixedFrame = context with { AccumulatorTicks = 0, DeltaTicks = 0, ElapsedTicks = 0 };

        try {
            // Validate sets the exit code from the pixel diff (0 pass, 1 gate-fail); only an infra failure below
            // overrides it with 2.
            Validate(vulkanContext: fixedFrame);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"WORLD-PARITY infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void Validate(FrameContext vulkanContext) {
        // Ensure the artifact directory exists; a failure here is caught by ProduceFrame and reported as exit-2 infra-fail.
        _ = Directory.CreateDirectory(path: m_artifactDirectory);

        // Generate the fuzz program HERE (inside ProduceFrame's try/catch) rather than in the constructor, so a
        // malformed generation envelope surfaces as the documented exit-2 infra-fail instead of an unhandled exception
        // during DI activation. The seed is deterministic, so both backends below build the identical program.
        m_program = ((m_fuzzSeed is int fuzzSeedValue) ? FuzzSdfProgram.Generate(bounds: m_bounds, seed: fuzzSeedValue) : null);

        // Vulkan reference: the host device (resolved from the fixed frame's host context) + the demo's Vulkan
        // compute services, with the SPIR-V kernels.
        byte[] vulkanPixels;

        using (var vulkanNode = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.spv")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.spv")),
            capturePath: m_vulkanCapturePath,
            children: (m_withChild ? ChildSurfaceNode.CreateWorldChildren(serviceProvider: m_serviceProvider, directX: false) : null),
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.spv")),
            frameSource: CreateFrameSource(),
            height: WorldHeight,
            serviceProvider: m_serviceProvider,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-views.comp.spv")),
            width: WorldWidth
        )) {
            _ = vulkanNode.ProduceFrame(context: in vulkanContext);
            vulkanPixels = vulkanNode.CapturedPixels.ToArray();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.Out.WriteLine(value: $"WORLD-PARITY | Vulkan -> {m_vulkanCapturePath}; Direct3D 12 unavailable on this OS (Vulkan-only).");
            m_result.ExitCode = 0;

            return;
        }

        var directXPixels = RenderDirectX();
        var width = (int)WorldWidth;
        var height = (int)WorldHeight;

        // Vulkan is the reference; DirectX is the comparand. The world composite is a continuous-shading colour
        // image with a richer benign-noise baseline than the graphics debug views, so it has its own calibrated set.
        // Fuzzing spans the whole input space, where benign ±1-LSB noise clusters along gradients — so it keys on the
        // max-delta / unit-delta benign signature (the WorldFuzz set) instead of the showcase isolation guard.
        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: width, height: height);
        var thresholds = (m_thresholds ?? ((m_fuzzSeed is null) ? ParityThresholds.WorldComposite : ParityThresholds.WorldFuzz));
        var failures = thresholds.Evaluate(metrics: metrics);
        var verdict = ((failures.Count == 0) ? "pass" : "fail");

        ParityValidationNode.WriteDiffImage(comparand: directXPixels, height: height, path: m_diffCapturePath, reference: vulkanPixels, width: width);

        m_result.ExitCode = ((failures.Count == 0) ? 0 : 1);

        // Content-sanity: the luma standard deviation of the reference. A near-zero value means a degenerate
        // (flat/black) frame — identical-degenerate output passes the cross-backend diff, so the fuzzer flags it as
        // low-signal rather than counting it as meaningful coverage.
        var coverage = FrameCoverage(rgba: vulkanPixels);
        var tag = ((m_fuzzSeed is int seed) ? $"FUZZ seed {seed} | " : string.Empty);
        var label = ((m_fuzzSeed is null) ? "WORLD-PARITY" : "WORLD-FUZZ");

        Console.Out.WriteLine(value: $"{label} {verdict} | {tag}diff {metrics.PercentDiffering:0.##}% ({metrics.DifferingPixels}px) | maxΔ{metrics.MaxChannelDelta} | isolated {(metrics.IsolatedFraction * 100.0):0}% | cov {coverage:0.0}{((coverage < 1.0) ? " DEGENERATE" : string.Empty)} | Vulkan -> {m_vulkanCapturePath}, Direct3D 12 -> {m_directXCapturePath}, diff -> {m_diffCapturePath}");

        if (failures.Count != 0) {
            Console.Error.WriteLine(value: $"{label} failures: {tag}{string.Join(separator: "; ", values: failures)}");
        }
    }
    // Luma standard deviation of an RGBA frame — a cheap "is there signal" proxy. ~0 for a flat/all-black frame.
    private static double FrameCoverage(byte[] rgba) {
        if (rgba.Length < 4) {
            return 0.0;
        }

        var pixels = (rgba.Length / 4);
        var sum = 0.0;
        var sumOfSquares = 0.0;

        for (var index = 0; (index < rgba.Length); index += 4) {
            var luma = ((0.299 * rgba[index]) + (0.587 * rgba[index + 1]) + (0.114 * rgba[index + 2]));

            sum += luma;
            sumOfSquares += (luma * luma);
        }

        var mean = (sum / pixels);

        return Math.Sqrt(Math.Max(0.0, ((sumOfSquares / pixels) - (mean * mean))));
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private byte[] RenderDirectX() {
        // A bespoke Direct3D 12 device on the same adapter (LUID-matched to the Vulkan host) + the D3D12 compute
        // services, with the DXIL kernels — the identical neutral node, captured for comparison with Vulkan.
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
        var directXContext = new FrameContext(
            AccumulatorTicks: 0,
            DeltaTicks: 0,
            ElapsedTicks: 0,
            Host: directX.Host,
            StepTicks: 1,
            TargetHeight: WorldHeight,
            TargetWidth: WorldWidth
        );

        using var directXNode = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.dxil")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.dxil")),
            capturePath: m_directXCapturePath,
            children: (m_withChild ? ChildSurfaceNode.CreateWorldChildren(serviceProvider: directX.Services, directX: true) : null),
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.dxil")),
            frameSource: CreateFrameSource(),
            height: WorldHeight,
            serviceProvider: directX.Services,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-views.comp.dxil")),
            width: WorldWidth
        );

        try {
            _ = directXNode.ProduceFrame(context: in directXContext);
        } catch {
            // A Direct3D 12 device removal surfaces only as the bare DEVICE_REMOVED HRESULT; query the SPECIFIC reason
            // (DEVICE_HUNG = a GPU timeout from too much work, DRIVER_INTERNAL_ERROR = invalid GPU work / a page fault,
            // ...) so the next cross-backend world regression is diagnosable from this gate's output, not opaque.
            var removedReason = new DirectXNativeDeviceApi().GetDeviceRemovedReason(deviceHandle: directX.DeviceContext.DeviceHandle);

            if (0 != removedReason) {
                Console.Error.WriteLine(value: $"WORLD-PARITY Direct3D 12 device removed | reason 0x{removedReason:X8}");
            }

            throw;
        }

        return directXNode.CapturedPixels.ToArray();
    }
}
