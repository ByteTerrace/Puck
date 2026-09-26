using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Whether the views' last render resolved external images to their fills.
    private bool m_viewsRenderedFilling;

    // Whether an external image resolves to its capture fill this frame.
    private bool FillsExternal => m_captureGate.Fills(content: ImageContentClass.External);

    // Whether any screen shows external content: a camera, a capture or a probe output.
    private bool BindsExternal() {
        foreach (var slot in m_slots.Values) {
            if (WorldCaptureFills.IsExternal(source: ShownOf(screen: slot.Index))) {
                return true;
            }
        }

        return false;
    }
    // Whether a screen shows external content or a HUD frame names it: the consumers WorldCaptureFills converts the fills
    // for before any capture is armed.
    private bool ConsumesExternal() {
        if (BindsExternal()) {
            return true;
        }

        foreach (var (source, _) in m_frameSourceReferences.Keys) {
            if (WorldCaptureFills.IsExternal(source: source)) {
                return true;
            }
        }

        return false;
    }
    // Converts the fill of every external source a screen shows or a HUD frame names whenever one does, not only while
    // the gate fills (WorldCaptureFills.Begin says why): the default fill, then each fill color a screen's source declares.
    private void EnsureFills(in FrameContext context) {
        var runtime = Runtime;

        if (!m_fills.Begin(
            context: in context,
            runtime: runtime
        )) {
            return;
        }

        foreach (var slot in m_slots.Values) {
            if (
                (ReadOf(screen: slot.Index) is { } instance) &&
                (FeedOf(instance: instance) is { Descriptor.FillsCaptures: true } source)
            ) {
                m_fills.Ensure(
                    context: in context,
                    rgba: source.Descriptor.CaptureFill,
                    runtime: runtime
                );
            }
        }
    }
    private Vector3 ResolveLight(IWorldImageFeed feed) => (m_captureGate.Fills(content: feed.Descriptor.Content)
        ? WorldImageLight.OfFill(rgba: feed.Descriptor.CaptureFill)
        : feed.Light
    );

    // The camera producer: a screen names a seat and a sensor, and the binder's shared per-device feeds serve it; the
    // feed a slot holds is only that (seat, sensor) reference, resolved every frame since the seat's device can change.
    private sealed class CameraProducer(WorldScreenBinder binder) : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CameraId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            var (settings, refusal) = WorldImageProducerSettings.Bind<WorldCameraSettings>(producer: source);

            if (settings is null) {
                feed = null;
                fault = refusal;

                return false;
            }

            if (!binder.m_cameraCapture.IsSupported) {
                feed = null;
                fault = "no camera device present";

                return false;
            }

            feed = new WorldCameraSourceFeed(
                cameras: binder,
                profile: settings.Profile,
                seat: (settings.Seat ?? DefaultViewSeat),
                sensor: settings.Sensor
            );
            fault = null;

            return true;
        }
    }
    // The desktop-capture producer: a window keyed by title or a whole monitor keyed by index, opened through the one
    // capture open ladder (TryCreateCaptureFeed), which retains a pending feed for a target not yet present. A capture a
    // live verb already opened to prove its target is adopted rather than opened again.
    private sealed class CaptureProducer(WorldScreenBinder binder) : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CaptureId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            var (settings, refusal) = WorldImageProducerSettings.Bind<WorldCaptureSettings>(producer: source);

            if (settings is null) {
                feed = null;
                fault = refusal;

                return false;
            }

            if (binder.TryClaimParkedCapture(
                capture: out var parked,
                source: source
            )) {
                feed = new CaptureSlotFeed(
                    binder: binder,
                    feed: parked
                );
                fault = null;

                return true;
            }

            if (binder.TryCreateCaptureFeed(
                capture: settings,
                fault: out fault
            ) is not { } capture) {
                feed = null;

                return false;
            }

            feed = new CaptureSlotFeed(
                binder: binder,
                feed: capture
            );

            return true;
        }
    }
    // One screen's own window or monitor capture: it pulls on the capture's cadence when published and is disposed with
    // the slot. It is an imported source on either route: the CPU tier a Vulkan host takes is the import's staged-copy
    // fallback, as the camera's CPU tier is, so its descriptor agrees with the producer's registered shape.
    private sealed class CaptureSlotFeed : IWorldImportFeed {
        private readonly WorldScreenBinder m_binder;

        public CaptureSlotFeed(WorldScreenBinder binder, CaptureFeed feed) {
            m_binder = binder;
            Feed = feed;
            Descriptor = new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Rate(rateHz: feed.Profile.RefreshRateHz),
                Color: ImageColorEncoding.Srgb,
                Content: ImageContentClass.External,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: ((uint)feed.Profile.Height),
                Producer: WorldImageProducerSettings.CaptureId,
                Transport: ImageSourceTransport.Imported,
                Width: ((uint)feed.Profile.Width)
            );
        }

        public ImageSourceDescriptor Descriptor { get; }
        public string? Fault => (Feed.Live
            ? null
            : Feed.Fault
        );
        public CaptureFeed Feed { get; }
        public Vector3 Light => Feed.Light;

        public GpuImageLease AcquireFrame() => Feed.AcquireFrame();
        public void Dispose() => Feed.Dispose();
        public nint Handle() => Feed.Handle();
        public void NotifyDeviceLost() => Feed.NotifyDeviceLost();
        public void Publish(in FrameContext context) {
            if (Feed.ShouldPull()) {
                m_binder.CaptureWindow(
                    context: in context,
                    feed: Feed
                );
            }
        }
    }
}
