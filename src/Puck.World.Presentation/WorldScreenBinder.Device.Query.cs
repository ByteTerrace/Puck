using Puck.Platform;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // How a running feed's image crosses from its producer's device to the render device, or null for one that crosses
    // no devices.
    private SharedFenceOrder? FenceOrderOf(IWorldImageFeed feed) => (feed switch {
        WorldCameraSourceFeed camera => CameraFenceOrderFor(
            seat: camera.Seat,
            sensor: camera.Sensor
        ),
        CaptureSlotFeed { Feed: { RidesGpu: true, Source: { } source } } => source.GpuFenceOrder,
        ProbeSourceFeed probe => probe.Feed.Order,
        _ => null,
    });
    // Whether a capture is armed this frame, which fills every external image.
    private bool CaptureArmed() => (Runtime?.PendingCapturePath is not null);
}