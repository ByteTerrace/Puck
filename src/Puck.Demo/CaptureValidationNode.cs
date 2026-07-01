using System.Runtime.Versioning;
using Puck.Abstractions.Capture;
using Puck.Abstractions.Presentation;
using Puck.Capture;
using Puck.Hosting;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Demo;

/// <summary>
/// A one-shot gate (installed under <c>--validate-capture</c>) for native image capture. It drives the whole
/// backend-neutral capture pipeline end to end — a GDI screen grab through the neutral
/// <see cref="IFrameCaptureSource"/> seam into a <see cref="CaptureSink"/> (with a <see cref="FrameHashObserver"/>),
/// writing <c>artifacts/capture-desktop.png</c>. It proves the pipeline wiring; it is lenient about signal
/// (a headless or secure desktop legitimately yields nothing), so it fails (exit 2) only on an actual error.
/// 0 = pass/skip, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class CaptureValidationNode : IRenderNode {
    private const int TargetHeight = 720;
    private const int TargetWidth = 1280;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "capture-validation",
        SurfaceId: SurfaceId.New()
    );
    private bool m_done;
    private readonly ParityResult m_result;

    /// <summary>Initializes a new instance of the <see cref="CaptureValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (unused; the gate self-constructs its capture backend).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CaptureValidationNode(IServiceProvider serviceProvider, ParityResult result) {
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
                Validate(ticks: context.RenderTicks);
            } else {
                Console.Out.WriteLine(value: "CAPTURE skip | native image capture requires Windows");
            }

            m_result.ExitCode = 0;
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CAPTURE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows")]
    private void Validate(ulong ticks) {
        if (!new Win32NativeImageCaptureService().TryCreateScreenCapture(
            height: TargetHeight,
            session: out var session,
            width: TargetWidth
        )) {
            Console.Out.WriteLine(value: "CAPTURE skip | no screen-capture session (headless or secure desktop)");
            return;
        }

        var path = Path.Combine(
            path1: Environment.CurrentDirectory,
            path2: "artifacts",
            path3: "capture-desktop.png"
        );

        using var source = new NativeImageCaptureSource(session: session);
        using var sink = new CaptureSink(
            observers: [new FrameHashObserver()],
            options: new CaptureOptions { OutputPath = path }
        );

        for (var attempt = 0; (attempt < 8); attempt++) {
            if (source.TryCapture(out var surface)) {
                sink.Consume(frame: new CaptureFrame(
                    FrameIndex: 0,
                    Surface: surface,
                    TimestampTicks: ticks
                ));
                Console.Out.WriteLine(value: $"CAPTURE pass | {TargetWidth}x{TargetHeight} desktop via GDI native image capture -> {path}");
                return;
            }
        }

        Console.Out.WriteLine(value: "CAPTURE skip | screen produced no frame within the retry budget");
    }
}
