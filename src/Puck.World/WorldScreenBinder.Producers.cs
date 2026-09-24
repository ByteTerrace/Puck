using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.SdfVm.Views;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Whether the views' last render resolved external images to their fills.
    private bool m_viewsRenderedFilling;

    // Whether an external image resolves to its capture fill this frame.
    private bool FillsExternal => m_captureGate.Fills(content: ImageContentClass.External);

    // Whether any screen shows external content: a live feed, a probe output, or an external declared feed.
    private bool BindsExternal() {
        foreach (var slot in m_slots.Values) {
            if (
                (slot.LiveFeed is not null) ||
                (slot.Probe is not null) ||
                (slot.DeclaredFeed is { Descriptor.FillsCaptures: true })
            ) {
                return true;
            }
        }

        return false;
    }
    // Opens a producer source's feed through the registry and places it: an external feed is the slot's live feed, any
    // other its declared feed. A producer that cannot open leaves its fault on the slot.
    private bool OpenProducer(ScreenSlot slot, WorldScreenSource.Producer source) {
        if (!m_producers.TryOpen(
            fault: out var fault,
            feed: out var feed,
            screenIndex: slot.Index,
            source: source
        )) {
            slot.DeclaredFault = fault;

            return false;
        }

        if (feed!.Descriptor.Content == ImageContentClass.External) {
            slot.LiveFeed?.Dispose();
            slot.LiveFeed = feed;
        } else {
            slot.ReleaseDeclared();
            slot.DeclaredFeed = feed;
        }

        slot.DeclaredFault = null;

        return true;
    }
    // The reconcile/select-side producer bind: the previous live feed and declared feed both give way to the new
    // source, so the slot shows exactly what the document now names.
    private (bool Ok, string Message) ApplyProducer(int index, ScreenSlot slot, WorldScreenSource.Producer source) {
        slot.ClearLive();
        slot.ReleaseDeclared();

        return (OpenProducer(
            slot: slot,
            source: source
        )
            ? (Ok: true, Message: $"screen {index} showing producer '{source.Id}'")
            : (Ok: false, Message: $"screen {index} producer '{source.Id}': {slot.DeclaredFault}")
        );
    }
    // Ensures the fill image of every external feed is uploaded while the gate fills, so a filled source never samples
    // an unset handle; a device loss drops the uploads and the next filled frame re-uploads them.
    private void EnsureFills(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (!m_captureGate.Filling) {
            return;
        }

        EnsureFill(
            deviceContext: deviceContext,
            gpu: gpu,
            rgba: ImageSourceDescriptor.DefaultCaptureFill
        );

        foreach (var slot in m_slots.Values) {
            if (slot.LiveFeed is { } live) {
                EnsureFill(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    rgba: live.Descriptor.CaptureFill
                );
            }

            if (slot.DeclaredFeed is { Descriptor.FillsCaptures: true } declared) {
                EnsureFill(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    rgba: declared.Descriptor.CaptureFill
                );
            }
        }
    }
    private void EnsureFill(IGpuDeviceContext deviceContext, IGpuComputeServices gpu, uint rgba) {
        if (!m_fills.TryGetValue(
            key: rgba,
            value: out var surface
        )) {
            surface = new CpuSurfaceSource();
            m_fills[rgba] = surface;
        }

        if (surface.CurrentHandle != 0) {
            return;
        }

        _ = surface.Publish(
            deviceContext: deviceContext,
            format: SurfaceFormat.R8G8B8A8Unorm,
            gpu: gpu,
            height: 1U,
            pixels: new[] { ((byte)rgba), ((byte)(rgba >> 8)), ((byte)(rgba >> 16)), ((byte)(rgba >> 24)) },
            width: 1U
        );
    }
    // The fill image of a packed RGBA8 color, or 0 (the procedural no-signal card, which shows no external pixels
    // either) before EnsureFills has uploaded it.
    private GpuImageLease FillImage(uint rgba) => (m_fills.TryGetValue(
        key: rgba,
        value: out var surface
    )
        ? surface.CurrentHandle
        : 0
    );
    private GpuImageLease Resolve(IWorldImageFeed feed) => m_captureGate.Resolve(
        feed: feed,
        fill: m_fillImage
    );
    private nint ResolveHandle(IWorldImageFeed feed) => (m_captureGate.Fills(content: feed.Descriptor.Content)
        ? FillImage(rgba: feed.Descriptor.CaptureFill).ImageViewHandle
        : feed.Handle()
    );
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

        public bool TryOpen(WorldScreenSource.Producer source, int screenIndex, out IWorldImageFeed? feed, out string? fault) {
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

            feed = new CameraSlotFeed(
                binder: binder,
                profile: settings.Profile,
                seat: (settings.Seat ?? DefaultViewSeat),
                sensor: settings.Sensor
            );
            fault = null;

            return true;
        }
    }
    // One screen's reference to a seat's shared camera feed. Publishing, device loss and disposal belong to the shared
    // feed the binder owns, so they are nothing here.
    private sealed class CameraSlotFeed : IWorldImageFeed {
        private readonly WorldScreenBinder m_binder;

        public CameraSlotFeed(WorldScreenBinder binder, WorldFeedProfile? profile, int seat, WorldCameraSensor sensor) {
            m_binder = binder;
            Seat = seat;
            Sensor = sensor;
            Descriptor = new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Rate(rateHz: (profile ?? WorldFeedProfile.Default).RefreshRateHz),
                Color: ImageColorEncoding.Srgb,
                Content: ImageContentClass.External,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: 0U,
                Producer: WorldImageProducerSettings.CameraId,
                Transport: ImageSourceTransport.Imported,
                Width: 0U
            );
        }

        public ImageSourceDescriptor Descriptor { get; }
        public string? Fault => m_binder.CameraFaultFor(
            seat: Seat,
            sensor: Sensor
        );
        public Vector3 Light => m_binder.CameraLightFor(
            seat: Seat,
            sensor: Sensor
        );
        public int Seat { get; }
        public WorldCameraSensor Sensor { get; }

        public GpuImageLease AcquireFrame() => m_binder.AcquireCameraFrame(
            seat: Seat,
            sensor: Sensor
        );
        public void Dispose() { }
        public nint Handle() => m_binder.CameraHandleFor(
            seat: Seat,
            sensor: Sensor
        );
        public void NotifyDeviceLost() { }
        public void Publish(ulong tick, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) { }
    }
    // The desktop-capture producer: a window keyed by title or a whole monitor keyed by index, opened through the one
    // capture open ladder (TryCreateCaptureFeed), which retains a pending feed for a target not yet present.
    private sealed class CaptureProducer(WorldScreenBinder binder) : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CaptureId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, int screenIndex, out IWorldImageFeed? feed, out string? fault) {
            var (settings, refusal) = WorldImageProducerSettings.Bind<WorldCaptureSettings>(producer: source);

            if (settings is null) {
                feed = null;
                fault = refusal;

                return false;
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
    // the slot.
    private sealed class CaptureSlotFeed : IWorldImageFeed {
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
                Transport: (feed.GpuRoute
                    ? ImageSourceTransport.Imported
                    : ImageSourceTransport.Uploaded
                ),
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

        public GpuImageLease AcquireFrame() => Feed.Handle();
        public void Dispose() => Feed.Dispose();
        public nint Handle() => Feed.Handle();
        public void NotifyDeviceLost() => Feed.NotifyDeviceLost();
        public void Publish(ulong tick, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
            if (Feed.ShouldPull()) {
                m_binder.CaptureWindow(
                    deviceContext: deviceContext,
                    feed: Feed,
                    gpu: gpu
                );
            }
        }
    }
}
