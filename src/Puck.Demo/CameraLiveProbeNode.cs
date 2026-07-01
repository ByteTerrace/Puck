using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.Hosting;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Demo;

/// <summary>
/// The live-camera HARDWARE bring-up gate (<c>--validate-camera-live</c>): opens the real default capture device through
/// the neutral <see cref="ICameraCaptureService"/> seam, polls it until an actual frame arrives (the grabber thread reads
/// asynchronously, so the first frame lags the open by a few sensor cycles), and writes it to
/// <c>artifacts/camera-live.png</c> so the RGB32 orientation and content can be eyeballed. This is the piece that could
/// only ever be exercised on a machine with a webcam — it verifies the end-to-end Media Foundation capture path
/// (enumerate → ActivateObject → media-type negotiation → ReadSample → ConvertToContiguousBuffer → Lock → publish) that
/// no amount of device-less testing could reach.
/// <para>It is lenient about the ENVIRONMENT (no camera, no Media Foundation, or a privacy-blocked device legitimately
/// yields nothing → skip) but strict about a real MALFUNCTION: a device that opens but then errors is an infra-fail.
/// 0 = pass/skip, 2 = infra-fail. It never presents.</para>
/// </summary>
internal sealed class CameraLiveProbeNode : IRenderNode {
    private const int FramePollBudget = 240; // ~4s at 16ms/attempt — generous for a cold sensor's first frame.
    private const int RequestedHeight = 720;
    private const int RequestedWidth = 1280;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "camera-live-probe",
        SurfaceId: SurfaceId.New()
    );
    private bool m_done;
    private readonly ParityResult m_result;

    /// <summary>Initializes a new instance of the <see cref="CameraLiveProbeNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (unused; the gate self-constructs its capture backend).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CameraLiveProbeNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() {
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            if (OperatingSystem.IsWindows()) {
                Probe();
            } else {
                Console.Out.WriteLine(value: "CAMERA-LIVE skip | live camera capture requires Windows (Media Foundation)");
            }

            m_result.ExitCode = 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CAMERA-LIVE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows")]
    private void Probe() {
        ICameraCaptureService service = new Win32MediaFoundationCameraService();

        if (!service.IsSupported || !service.TryOpenDefault(requestedWidth: RequestedWidth, requestedHeight: RequestedHeight, session: out var session)) {
            Console.Out.WriteLine(value: "CAMERA-LIVE skip | no capture device (or Media Foundation unavailable)");

            return;
        }

        using (session) {
            for (var attempt = 0; (attempt < FramePollBudget); attempt++) {
                if (session.TryCapture(out var surface) && !surface.Pixels.IsEmpty) {
                    WriteProbe(session: session, surface: surface);

                    return;
                }

                Thread.Sleep(millisecondsTimeout: 16);
            }
        }

        // The device opened but never produced a frame within the budget — the exact hardware failure this gate hunts.
        // Reported loudly (not an infra-fail: a privacy shutter or a busy device can cause it legitimately).
        Console.Out.WriteLine(value: $"CAMERA-LIVE no-frame | '{session.Name}' {session.Width}x{session.Height} opened but produced no frame within {FramePollBudget} attempts");
    }

    [SupportedOSPlatform("windows")]
    private void WriteProbe(ICameraCaptureSession session, Surface surface) {
        var height = (int)surface.Height;
        var width = (int)surface.Width;
        var pixels = surface.Pixels.Span;
        var tightRow = (width * 4);
        var stride = ((height > 0) ? (pixels.Length / height) : tightRow);
        // B8G8R8A8 (Media Foundation RGB32) -> R8G8B8A8 for the PNG encoder; also strips any row padding the contiguous
        // buffer carries (stride can exceed width*4).
        var rgba = new byte[tightRow * height];

        for (var y = 0; (y < height); y++) {
            var sourceRow = (y * stride);
            var destinationRow = (y * tightRow);

            for (var x = 0; (x < width); x++) {
                var source = (sourceRow + (x * 4));
                var destination = (destinationRow + (x * 4));

                rgba[destination + 0] = pixels[source + 2]; // R <- B
                rgba[destination + 1] = pixels[source + 1]; // G
                rgba[destination + 2] = pixels[source + 0]; // B <- R
                rgba[destination + 3] = 0xFF;               // opaque (RGB32 alpha is undefined)
            }
        }

        var path = Path.Combine(path1: Environment.CurrentDirectory, path2: "artifacts", path3: "camera-live.png");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        PngImage.Write(height: height, path: path, rgba: rgba, width: width);

        Console.Out.WriteLine(value: $"CAMERA-LIVE pass | '{session.Name}' {width}x{height} (stride {stride}B, {pixels.Length}B buffer) -> {path}");
    }
}
