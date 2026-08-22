using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Standalone Capture sources declared through DeclareFrameSource, keyed by the source record itself (WorldFrameSource
    // equality — Camera/View/Probe already have a stable identity elsewhere: EnsureCameraFeed keys by sensor,
    // RegisterCameraView by camera name, GetOrAddProbeFeed by probe id; a Capture carries no such name, so the record IS
    // its own key). Populated only on success — a source the platform cannot ever open (no window-capture support at
    // all) records no entry and is retried the next time DeclareFrameSource sees it (cheap: HUD structure rebuilds run
    // at most once per definition revision plus once per edited identity).
    private readonly Dictionary<WorldFrameSource, CaptureFeed> m_frameCaptures = new();

    /// <summary>Declares that a <see cref="WorldFrameSource"/> is consumed outside any declared <c>screens</c> row (a
    /// HUD/overlay <c>Frame</c> element) and opens its feed through the same shared machinery a screen row would —
    /// idempotent, safe to call at boot or on every definition/identity revision. A camera sensor no screen names is
    /// still one shared feed with any screen that later does (<see cref="EnsureCameraFeed"/> is itself idempotent per
    /// sensor); a view camera renders every <see cref="ViewStack"/> refresh with no wired screen narrowing its
    /// round-robin turn (<see cref="RegisterCameraView"/>'s default <c>isLive</c> is always-true); a probe reads
    /// whatever its own kernel publishes; a capture opens through <see cref="TryCreateCaptureFeed"/>, the same ladder
    /// a declared screen's capture source uses.</summary>
    /// <param name="source">The frame source a HUD element (or any other non-screen consumer) names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void DeclareFrameSource(WorldFrameSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (m_disposed) {
            return;
        }

        switch (source) {
            case WorldScreenSource.Camera camera:
                _ = EnsureCameraFeed(
                    profile: (camera.Profile ?? WorldFeedProfile.Default),
                    sensor: camera.Sensor
                );

                break;
            case WorldScreenSource.View view:
                if (ResolveCamera(name: view.CameraName) is { } resolvedCamera) {
                    RegisterCameraView(camera: resolvedCamera);
                }

                break;
            case WorldScreenSource.Probe probe:
                _ = GetOrAddProbeFeed(id: probe.Id);

                break;
            case WorldScreenSource.Capture capture:
                if (
                    !m_frameCaptures.ContainsKey(key: source) &&
                    (TryCreateCaptureFeed(capture: capture, fault: out _) is { } feed)
                ) {
                    m_frameCaptures[source] = feed;
                }

                break;
        }
    }
    /// <summary>Acquires the current frame for a previously-declared <see cref="WorldFrameSource"/> — the render-thread,
    /// per-frame counterpart of <see cref="DeclareFrameSource"/>. Reads the same shared feed a screen slot naming the
    /// identical source would (a camera sensor, a named view, a probe id, or a standalone capture), so a HUD frame and
    /// a diegetic screen filming the same source see the same image.</summary>
    /// <param name="source">The frame source to sample.</param>
    /// <param name="frame">The acquired frame, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the source is live this frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool TryAcquireFrame(WorldFrameSource source, out SdfScreenSourceFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: source);

        switch (source) {
            case WorldScreenSource.Camera camera:
                if (m_cameraFeeds.TryGetValue(key: camera.Sensor, value: out var cameraFeed)) {
                    frame = cameraFeed.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.View view:
                if (m_viewStack is { } stack) {
                    var handle = stack.Resolve(name: view.CameraName);

                    frame = handle;

                    return (0 != handle);
                }

                break;
            case WorldScreenSource.Probe probe:
                if (m_probeFeeds.TryGetValue(key: probe.Id, value: out var probeFeed)) {
                    frame = probeFeed.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.Capture:
                if (m_frameCaptures.TryGetValue(key: source, value: out var captureFeed)) {
                    var handle = captureFeed.Handle();

                    frame = handle;

                    return (0 != handle);
                }

                break;
        }

        frame = default;

        return false;
    }
    // Standalone captures ride the same per-frame pull cadence a slot-owned capture does, from Publish (below), and
    // the same device-lost/dispose sweeps every other feed this binder owns gets.
    private void PublishFrameCaptures(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        foreach (var feed in m_frameCaptures.Values) {
            if (feed.ShouldPull()) {
                CaptureWindow(
                    deviceContext: deviceContext,
                    feed: feed,
                    gpu: gpu
                );
            }
        }
    }
    private void DisposeFrameCaptures() {
        foreach (var feed in m_frameCaptures.Values) {
            feed.Dispose();
        }

        m_frameCaptures.Clear();
    }
    private void NotifyFrameCapturesDeviceLost() {
        foreach (var feed in m_frameCaptures.Values) {
            feed.NotifyDeviceLost();
        }
    }
}
