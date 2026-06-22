using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A one-shot cross-backend gate (installed under <c>--validate-viewports</c>) for the generic
/// <see cref="ViewportCompositorNode"/>. It composites a HETEROGENEOUS layout — a raw integer-copy pane
/// (<see cref="ChildSurfaceNode"/>, the test pattern at the pane's native resolution) beside a SAMPLED pane
/// (<see cref="ResampleNode"/>, the same pattern rendered small and NEAREST-upscaled, so it is unmistakably blocky)
/// — on BOTH backends: the Vulkan host device (SPIR-V) and a bespoke Direct3D 12 device (DXIL) LUID-matched to the
/// same adapter. It captures each to a PNG and asserts the two composites agree (every channel within ±1 LSB) — so
/// the source-agnostic compositor and the compute sampler produce the same heterogeneous frame cross-backend. This
/// is the Stage-3 proof that an arbitrary-resolution source can fill a viewport through the new compositor.
/// 0 = pass, 1 = cross-backend diff, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class ViewportParityNode : IRenderNode {
    private const uint ViewportHeight = 256;
    private const uint ViewportWidth = 512;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "viewport-parity",
        SurfaceId: SurfaceId.New()
    );
    private readonly string m_artifactDirectory;
    private readonly string m_directXCapturePath;
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private readonly string m_vulkanCapturePath;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="ViewportParityNode"/> class.</summary>
    /// <param name="serviceProvider">The demo's service provider (the Vulkan-hosted compute services + the LUID source for the bespoke Direct3D 12 device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public ViewportParityNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
        m_artifactDirectory = "artifacts";
        m_directXCapturePath = Path.Combine(path1: m_artifactDirectory, path2: "viewports-directx.png");
        m_vulkanCapturePath = Path.Combine(path1: m_artifactDirectory, path2: "viewports-vulkan.png");
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

        // A fixed delta-zero frame so both backends produce the identical static pattern.
        var fixedFrame = context with { AccumulatorTicks = 0, DeltaTicks = 0, ElapsedTicks = 0 };

        try {
            Validate(vulkanContext: fixedFrame);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"VIEWPORTS infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void Validate(FrameContext vulkanContext) {
        _ = Directory.CreateDirectory(path: m_artifactDirectory);

        // Vulkan reference: the host device + the demo's Vulkan compute services (SPIR-V kernels).
        byte[] vulkanPixels;

        using (var vulkanNode = BuildCompositor(serviceProvider: m_serviceProvider, capturePath: m_vulkanCapturePath, directX: false)) {
            _ = vulkanNode.ProduceFrame(context: in vulkanContext);
            vulkanPixels = vulkanNode.CapturedPixels.ToArray();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.Out.WriteLine(value: $"VIEWPORTS | Vulkan -> {m_vulkanCapturePath}; Direct3D 12 unavailable on this OS (Vulkan-only).");
            m_result.ExitCode = 0;

            return;
        }

        var directXPixels = RenderDirectX();
        var maxDelta = MaxAbsDelta(a: vulkanPixels, b: directXPixels);
        var differing = CountDiffering(a: vulkanPixels, b: directXPixels, tolerance: 0);
        var pass = (maxDelta <= 1);

        m_result.ExitCode = (pass ? 0 : 1);

        Console.Out.WriteLine(value: $"VIEWPORTS {(pass ? "pass" : "fail")} | {ViewportWidth}x{ViewportHeight} heterogeneous composite (raw child | NEAREST-resampled pane) cross-backend maxΔ{maxDelta} ({differing}px differ) | Vulkan -> {m_vulkanCapturePath}, Direct3D 12 -> {m_directXCapturePath}");

        if (!pass) {
            Console.Error.WriteLine(value: $"VIEWPORTS failure | cross-backend max channel delta {maxDelta} > 1 — the generic compositor or the compute sampler diverged across backends");
        }
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private byte[] RenderDirectX() {
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
        var directXContext = new FrameContext(
            AccumulatorTicks: 0,
            DeltaTicks: 0,
            ElapsedTicks: 0,
            Host: directX.Host,
            StepTicks: 1,
            TargetHeight: ViewportHeight,
            TargetWidth: ViewportWidth
        );

        using var directXNode = BuildCompositor(serviceProvider: directX.Services, capturePath: m_directXCapturePath, directX: true);

        _ = directXNode.ProduceFrame(context: in directXContext);

        return directXNode.CapturedPixels.ToArray();
    }

    // The heterogeneous layout: a left pane drawn raw (integer-copy) and a right pane the same pattern resampled small
    // and NEAREST-upscaled (visibly blocky). Both kernels are loaded for the target backend (SPIR-V or DXIL).
    private static ViewportCompositorNode BuildCompositor(IServiceProvider serviceProvider, string capturePath, bool directX) {
        var extension = (directX ? ".dxil" : ".spv");
        var childBytecode = File.ReadAllBytes(path: (Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-child.comp") + extension));
        var resampleBytecode = File.ReadAllBytes(path: (Path.Combine(path1: ShaderPath(folder: "Resample"), path2: "resample.comp") + extension));
        var compositeBytecode = File.ReadAllBytes(path: (Path.Combine(path1: ShaderPath(folder: "Viewport"), path2: "viewport-composite.comp") + extension));

        ViewportPane[] panes = [
            new ViewportPane(
                Region: new NormalizedRect(X: 0f, Y: 0f, Width: 0.5f, Height: 1f),
                Source: new ChildSurfaceNode(serviceProvider: serviceProvider, bytecode: childBytecode)
            ),
            new ViewportPane(
                Region: new NormalizedRect(X: 0.5f, Y: 0f, Width: 0.5f, Height: 1f),
                Source: new ResampleNode(serviceProvider: serviceProvider, childBytecode: childBytecode, resampleBytecode: resampleBytecode, filter: GpuSamplerFilter.Nearest, inputSize: 48)
            ),
        ];

        return new ViewportCompositorNode(
            capturePath: capturePath,
            compositeBytecode: compositeBytecode,
            height: ViewportHeight,
            panes: panes,
            serviceProvider: serviceProvider,
            width: ViewportWidth
        );
    }
    private static string ShaderPath(string folder) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", folder);
    private static int MaxAbsDelta(byte[] a, byte[] b) {
        if (a.Length != b.Length) {
            return int.MaxValue;
        }

        var max = 0;

        for (var index = 0; (index < a.Length); index++) {
            max = Math.Max(max, Math.Abs(a[index] - b[index]));
        }

        return max;
    }
    private static int CountDiffering(byte[] a, byte[] b, int tolerance) {
        if (a.Length != b.Length) {
            return int.MaxValue;
        }

        var count = 0;

        for (var index = 0; (index < a.Length); index += 4) {
            if (
                (Math.Abs(a[index] - b[index]) > tolerance) ||
                (Math.Abs(a[index + 1] - b[index + 1]) > tolerance) ||
                (Math.Abs(a[index + 2] - b[index + 2]) > tolerance)
            ) {
                count++;
            }
        }

        return count;
    }
}
