using System.Runtime.Versioning;
using Puck.Abstractions.Presentation;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A one-shot cross-backend gate (installed under <c>--validate-pixelate</c>) for the retro <see cref="PixelateNode"/>
/// decorator. It composites a raw pane (<see cref="ChildSurfaceNode"/>, the test pattern at full pane resolution)
/// beside a PIXELATED pane (the SAME pattern wrapped in a <see cref="PixelateNode"/> — cell-blocked and color-depth
/// reduced) on BOTH backends: the Vulkan host (SPIR-V) and a bespoke Direct3D 12 device (DXIL). The source is the
/// deterministic <c>sdf-child</c> pattern and pixelation is integer cell-snap + posterize, so the composite is
/// BIT-IDENTICAL cross-backend; it captures each to a PNG and asserts they agree within ±1 LSB. Proves the Stage-6
/// pixelate decorator works (any node can be retro-ified through it) on both backends.
/// 0 = pass, 1 = cross-backend diff, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class PixelateParityNode : IRenderNode {
    private const uint CellSize = 10;        // chunky 10px blocks
    private const uint QuantizeLevels = 6;   // 6 levels per channel (posterized palette)
    private const uint ViewportHeight = 256;
    private const uint ViewportWidth = 512;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "pixelate-parity",
        SurfaceId: SurfaceId.New()
    );
    private readonly string m_artifactDirectory;
    private readonly string m_directXCapturePath;
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private readonly string m_vulkanCapturePath;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="PixelateParityNode"/> class.</summary>
    /// <param name="serviceProvider">The demo's service provider (the Vulkan-hosted compute services + the LUID source for the bespoke Direct3D 12 device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public PixelateParityNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
        m_artifactDirectory = "artifacts";
        m_directXCapturePath = Path.Combine(path1: m_artifactDirectory, path2: "pixelate-directx.png");
        m_vulkanCapturePath = Path.Combine(path1: m_artifactDirectory, path2: "pixelate-vulkan.png");
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

        var fixedFrame = context with { AccumulatorTicks = 0, DeltaTicks = 0, ElapsedTicks = 0 };

        try {
            Validate(vulkanContext: fixedFrame);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"PIXELATE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void Validate(FrameContext vulkanContext) {
        _ = Directory.CreateDirectory(path: m_artifactDirectory);

        byte[] vulkanPixels;

        using (var vulkanNode = BuildCompositor(serviceProvider: m_serviceProvider, capturePath: m_vulkanCapturePath, directX: false)) {
            _ = vulkanNode.ProduceFrame(context: in vulkanContext);
            vulkanPixels = vulkanNode.CapturedPixels.ToArray();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            Console.Out.WriteLine(value: $"PIXELATE | Vulkan -> {m_vulkanCapturePath}; Direct3D 12 unavailable on this OS (Vulkan-only).");
            m_result.ExitCode = 0;

            return;
        }

        var directXPixels = RenderDirectX();
        var maxDelta = MaxAbsDelta(a: vulkanPixels, b: directXPixels);
        var pass = (maxDelta <= 1);

        m_result.ExitCode = (pass ? 0 : 1);

        Console.Out.WriteLine(value: $"PIXELATE {(pass ? "pass" : "fail")} | {ViewportWidth}x{ViewportHeight} (raw child | {CellSize}px cells, {QuantizeLevels}-level palette) cross-backend maxΔ{maxDelta} | Vulkan -> {m_vulkanCapturePath}, Direct3D 12 -> {m_directXCapturePath}");

        if (!pass) {
            Console.Error.WriteLine(value: $"PIXELATE failure | cross-backend max channel delta {maxDelta} > 1 — the pixelate decorator diverged across backends");
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

    // The layout: a left pane drawn raw, and a right pane the same pattern wrapped in a PixelateNode (cell-blocked +
    // posterized). Both kernels are loaded for the target backend (SPIR-V or DXIL).
    private static ViewportCompositorNode BuildCompositor(IServiceProvider serviceProvider, string capturePath, bool directX) {
        var extension = (directX ? ".dxil" : ".spv");
        var childBytecode = File.ReadAllBytes(path: (Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-child.comp") + extension));
        var pixelateBytecode = File.ReadAllBytes(path: (Path.Combine(path1: ShaderPath(folder: "Viewport"), path2: "pixelate.comp") + extension));
        var compositeBytecode = File.ReadAllBytes(path: (Path.Combine(path1: ShaderPath(folder: "Viewport"), path2: "viewport-composite.comp") + extension));

        ViewportPane[] panes = [
            new ViewportPane(
                Region: new NormalizedRect(X: 0f, Y: 0f, Width: 0.5f, Height: 1f),
                Source: new ChildSurfaceNode(serviceProvider: serviceProvider, bytecode: childBytecode)
            ),
            new ViewportPane(
                Region: new NormalizedRect(X: 0.5f, Y: 0f, Width: 0.5f, Height: 1f),
                Source: new PixelateNode(
                    cellSize: CellSize,
                    pixelateBytecode: pixelateBytecode,
                    quantizeLevels: QuantizeLevels,
                    serviceProvider: serviceProvider,
                    source: new ChildSurfaceNode(serviceProvider: serviceProvider, bytecode: childBytecode)
                )
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
}
