using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A one-shot gate (installed under <c>--validate-capture</c>) for live screen capture via the DXGI Desktop
/// Duplication API. It creates a <see cref="DesktopDuplicationCapture"/>, grabs one desktop frame into a shared
/// (NT-handle) BGRA texture on the GPU — zero-copy, hardware-composited content and all — reads it back to a PNG,
/// and asserts the frame carries real signal (a non-degenerate luma spread). This proves the capture half of the
/// viewport-source seam: the captured texture's shared handle is what a D3D12 host imports via <c>OpenSharedHandle</c>
/// to fill a viewport pane. 0 = pass, 2 = infra-fail (or no desktop frame). It never presents.
/// </summary>
internal sealed class CaptureValidationNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "desktop-capture-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="CaptureValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the LUID source for the bespoke Direct3D 12 host the capture is shared into).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CaptureValidationNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
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

        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
                Console.Out.WriteLine(value: "CAPTURE skip | Desktop Duplication requires Windows 10+");
                m_result.ExitCode = 0;
            } else {
                Validate();
                m_result.ExitCode = 0;
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CAPTURE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        var directory = "artifacts";

        _ = Directory.CreateDirectory(path: directory);

        var path = Path.Combine(path1: directory, path2: "capture-desktop.png");

        // Pin the capture's Direct3D 11 device to the host's GPU (the LUID the Vulkan host runs on), so a future
        // zero-copy hand-off shares the same adapter. The capture itself is GPU Desktop Duplication; the gate reads it
        // back to a PNG for verification.
        using var host = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);

        var hostLuid = new DirectXNativeDeviceApi().GetAdapterLuid(deviceHandle: host.DeviceContext.DeviceHandle);

        using var capture = new DesktopDuplicationCapture(adapterLuid: hostLuid);

        if (!capture.CaptureFrame()) {
            throw new InvalidOperationException(message: "Desktop Duplication produced no frame within the retry budget (a fully static desktop, or a secure-desktop transition).");
        }

        var rgba = BgraToRgba(bgra: capture.ReadbackBgra());

        PngImage.Write(
            height: (int)capture.Height,
            path: path,
            rgba: rgba,
            width: (int)capture.Width
        );

        var coverage = LumaStandardDeviation(rgba: rgba);

        if (coverage < 1.0) {
            throw new InvalidOperationException(message: $"The captured desktop is degenerate (luma stddev {coverage:0.0}) — no signal in the frame.");
        }

        Console.Out.WriteLine(value: $"CAPTURE pass | {capture.Width}x{capture.Height} live desktop via DXGI Desktop Duplication on '{capture.AdapterDescription}' (GPU capture of hardware-composited content) | cov {coverage:0.0} -> {path}");
    }

    // GDI/DXGI desktop pixels are BGRA; the PNG writer (and the engine) are RGBA. Swap the B and R channels.
    private static byte[] BgraToRgba(byte[] bgra) {
        var rgba = new byte[bgra.Length];

        for (var index = 0; (index < bgra.Length); index += 4) {
            rgba[index + 0] = bgra[index + 2];
            rgba[index + 1] = bgra[index + 1];
            rgba[index + 2] = bgra[index + 0];
            rgba[index + 3] = byte.MaxValue;
        }

        return rgba;
    }

    // Luma standard deviation — a cheap "is there signal" proxy; ~0 for a flat/black frame.
    private static double LumaStandardDeviation(byte[] rgba) {
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
}
