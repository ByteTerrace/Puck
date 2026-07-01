using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// The cross-backend parity gate: a one-shot root render node installed only under <c>--validate</c>. On its
/// first frame it builds both SDF producers (a headless, LUID-matched Direct3D 12 producer and a plain offscreen
/// Vulkan producer on the host device), renders and reads each back to host RGBA — Direct3D 12 fully first, then
/// Vulkan, so the host queue stays quiet — diffs them tolerance-aware, writes artifacts, records the verdict in
/// the shared <see cref="ParityResult"/>, and then asks the terminal to exit. It never presents.
/// </summary>
internal sealed class ParityValidationNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "parity-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="ParityValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the Vulkan device for the LUID match and host render).</param>
    /// <param name="result">The shared result the verdict and exit code are written to.</param>
    public ParityValidationNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
    }

    /// <summary>The directory parity artifacts are written to (relative to the working directory; gitignored).</summary>
    internal static string ArtifactDirectory => Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "parity"
    );

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

        try {
            Validate();
        } catch (Exception exception) {
            // Infra failure (e.g. no Direct3D 12 device present): fail loudly, never silently pass.
            Console.Error.WriteLine(value: $"PARITY infra-fail | {exception.Message}");
            m_result.ExitCode = 2;
        }

        // Write the result before requesting exit so the exit code is always observable.
        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void Validate() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            throw new PlatformNotSupportedException(message: "Cross-backend parity validation requires Windows 10.0.10240+ for the Direct3D 12 backend.");
        }

        // A fresh, empty artifacts directory each run so stale PNGs never masquerade as this run's output.
        if (Directory.Exists(path: ArtifactDirectory)) {
            Directory.Delete(path: ArtifactDirectory, recursive: true);
        }

        Directory.CreateDirectory(path: ArtifactDirectory);

        // Build both producers once; only the push constant (the debug view mode) changes per mode, so the
        // resources persist across the loop.
        using var directXProducer = CrossBackendShowcase.CreateDirectXProducer(serviceProvider: m_serviceProvider, shaderDirectory: CrossBackendShowcase.ShaderDirectory, capturePath: null);
        using var vulkanProducer = CrossBackendShowcase.CreateVulkanProducer(serviceProvider: m_serviceProvider, shaderDirectory: CrossBackendShowcase.ShaderDirectory, capturePath: null);

        var modeReports = new Dictionary<string, ParityModeReport>();
        var anyFailure = false;
        var width = 0;
        var height = 0;

        for (var mode = 0; (mode < DebugViewModes.Count); mode++) {
            var name = DebugViewModes.Name(mode: mode);

            // Render Direct3D 12 fully first (its own headless device/queue), then Vulkan (the host device), so
            // the host queue is never racing an offscreen submit while it owns the swapchain.
            directXProducer.DebugMode = mode;

            var directXPixels = directXProducer.RenderToBuffer();

            vulkanProducer.DebugMode = mode;

            var vulkanPixels = vulkanProducer.RenderToBuffer();

            if (
                (directXProducer.Width != vulkanProducer.Width) ||
                (directXProducer.Height != vulkanProducer.Height)
            ) {
                throw new InvalidOperationException(message: $"Backend extents differ: DirectX {directXProducer.Width}x{directXProducer.Height} vs Vulkan {vulkanProducer.Width}x{vulkanProducer.Height}.");
            }

            width = (int)vulkanProducer.Width;
            height = (int)vulkanProducer.Height;

            // Vulkan is the reference; DirectX is the comparand. Continuous views use the strict ±1 thresholds;
            // discrete views (material-id, iteration-count) use the relaxed set.
            var metrics = ParityMetrics.Compute(reference: vulkanPixels.Span, comparand: directXPixels.Span, width: width, height: height);
            var thresholds = ParityThresholds.ForMode(mode: mode);
            var failures = thresholds.Evaluate(metrics: metrics);
            var verdict = ((failures.Count == 0) ? "pass" : "fail");

            anyFailure |= (failures.Count != 0);

            PngImage.Write(height: height, path: Path.Combine(ArtifactDirectory, $"vulkan-{name}.png"), rgba: vulkanPixels.Span, width: width);
            PngImage.Write(height: height, path: Path.Combine(ArtifactDirectory, $"directx-{name}.png"), rgba: directXPixels.Span, width: width);
            WriteDiffImage(path: Path.Combine(ArtifactDirectory, $"diff-{name}.png"), reference: vulkanPixels.Span, comparand: directXPixels.Span, width: width, height: height);

            modeReports[name] = BuildModeReport(verdict: verdict, metrics: metrics, failures: failures, thresholds: thresholds);

            Console.Out.WriteLine(value: $"PARITY {name,-16} {verdict} | diff {metrics.PercentDiffering:0.##}% ({metrics.DifferingPixels}px) | maxΔ{metrics.MaxChannelDelta} | isolated {(metrics.IsolatedFraction * 100.0):0}%");
        }

        var overallVerdict = (anyFailure ? "fail" : "pass");

        WriteReport(verdict: overallVerdict, width: width, height: height, modeReports: modeReports);

        m_result.ExitCode = (anyFailure ? 1 : 0);

        // Overall digest; the per-mode diff heatmaps are amplified 64× (faint = benign ±1 noise), authoritative
        // numbers live in report.json.
        Console.Out.WriteLine(value: $"PARITY {overallVerdict} | {DebugViewModes.Count} modes | artifacts/parity/");
    }

    // A grayscale max-channel-delta heatmap, amplified so divergences glow without a 1-LSB image looking
    // alarming: value = min(255, d * 64). Shared with WorldParityNode.
    internal static void WriteDiffImage(string path, ReadOnlySpan<byte> reference, ReadOnlySpan<byte> comparand, int width, int height) {
        var pixelCount = (width * height);
        var diff = new byte[pixelCount * 4];

        for (var pixel = 0; (pixel < pixelCount); pixel++) {
            var offset = (pixel * 4);
            var deltaR = Math.Abs(reference[offset] - comparand[offset]);
            var deltaG = Math.Abs(reference[offset + 1] - comparand[offset + 1]);
            var deltaB = Math.Abs(reference[offset + 2] - comparand[offset + 2]);
            var value = (byte)Math.Min(255, (Math.Max(deltaR, Math.Max(deltaG, deltaB)) * 64));

            diff[offset] = value;
            diff[offset + 1] = value;
            diff[offset + 2] = value;
            diff[offset + 3] = byte.MaxValue;
        }

        PngImage.Write(height: height, path: path, rgba: diff, width: width);
    }

    private static ParityModeReport BuildModeReport(string verdict, ParityMetrics metrics, IReadOnlyList<string> failures, ParityThresholdSet thresholds) {
        return new ParityModeReport {
            Verdict = verdict,
            Failures = failures,
            Metrics = new ParityMetricsReport {
                TotalPixels = metrics.TotalPixels,
                DifferingPixels = metrics.DifferingPixels,
                PercentDiffering = metrics.PercentDiffering,
                MaxChannelDelta = metrics.MaxChannelDelta,
                MeanAbsError = metrics.MeanAbsError,
                IsolatedFraction = metrics.IsolatedFraction,
                UnitDeltaFraction = metrics.UnitDeltaFraction,
                DeltaHistogram = metrics.DeltaHistogram,
            },
            Thresholds = new ParityThresholdsReport {
                MaxChannelDelta = thresholds.MaxChannelDelta,
                PercentDiffering = thresholds.MaxPercentDiffering,
                UnitDeltaFraction = thresholds.MinUnitDeltaFraction,
                IsolatedFraction = thresholds.MinIsolatedFraction,
                MeanAbsError = thresholds.MaxMeanAbsError,
            },
        };
    }
    private static void WriteReport(string verdict, int width, int height, IReadOnlyDictionary<string, ParityModeReport> modeReports) {
        var report = new ParityReport {
            Verdict = verdict,
            Width = width,
            Height = height,
            Modes = modeReports,
        };

        File.WriteAllText(
            contents: JsonSerializer.Serialize(value: report, jsonTypeInfo: ParityReportJsonContext.Default.ParityReport),
            path: Path.Combine(ArtifactDirectory, "report.json")
        );
    }
}
