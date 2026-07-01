using System.Runtime.Versioning;
using Puck.Abstractions.Capture;
using Puck.Capture;
using Puck.Platform;
using Puck.Platform.Windows;

namespace Puck.Post;

/// <summary>
/// Tier-B stage B5. Native image capture, environment-lenient: drives the whole backend-neutral capture pipeline end
/// to end — a GDI screen grab through the neutral <see cref="IFrameCaptureSource"/> seam into a
/// <see cref="CaptureSink"/> (with a <see cref="FrameHashObserver"/>), writing <c>capture-desktop.png</c> into the
/// POST's artifacts. It proves the pipeline WIRING; signal is environmental (a headless or secure desktop
/// legitimately yields nothing), so no-signal outcomes are a <see cref="PostVerdict.Skip"/>, never a
/// <see cref="PostVerdict.Fail"/> — only an actual error fails (and that surfaces as the battery's
/// <see cref="PostVerdict.Infra"/> catch).
/// </summary>
internal sealed class CaptureStage : IPostStage {
    private const int TargetHeight = 720;
    private const int TargetWidth = 1280;

    /// <inheritdoc/>
    public string Name => "capture";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindows()) {
            return PostStageOutcome.Skip(detail: "native image capture requires Windows");
        }

        return Capture(context: context);
    }

    [SupportedOSPlatform("windows")]
    private static PostStageOutcome Capture(PostContext context) {
        if (!new Win32NativeImageCaptureService().TryCreateScreenCapture(
            height: TargetHeight,
            session: out var session,
            width: TargetWidth
        )) {
            return PostStageOutcome.Skip(detail: "no screen-capture session (headless or secure desktop)");
        }

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "capture-desktop.png");

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        using var source = new NativeImageCaptureSource(session: session);
        using var sink = new CaptureSink(
            observers: [new FrameHashObserver()],
            options: new CaptureOptions { OutputPath = artifactPath }
        );

        for (var attempt = 0; (attempt < 8); attempt++) {
            if (source.TryCapture(out var surface)) {
                sink.Consume(frame: new CaptureFrame(
                    FrameIndex: 0,
                    Surface: surface,
                    TimestampTicks: 0
                ));

                return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"{TargetWidth}x{TargetHeight} desktop via GDI native image capture");
            }
        }

        return PostStageOutcome.Skip(detail: "the screen produced no frame within the retry budget");
    }
}
