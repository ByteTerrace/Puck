using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // Drops every known device's feed whose resolved seat and sensor is no longer demanded, and replaces one whose
    // demanded profile changed.
    private void RetireUndemandedCameraFeeds() {
        foreach (var device in m_cameraDeviceOrder) {
            var seat = ((m_roster.DeviceSlot(device: device.DeviceId) is { } slot)
                ? PlayerRoster.DisplayNumber(slot: slot)
                : 0
            );
            // A slot can carry more than one camera at once (TryGetSeatDevice's own most-recently-assigned
            // tie-break exists precisely because PlayerRoster allows this) — so mapping to a demanding seat is not
            // enough to keep a feed open. Only the seat's RESOLVED device (the one TryFulfillCameraDemand/
            // TryResolveCamera would themselves pick) may hold one; a co-habiting camera that lost the tie-break
            // (the previous winner, after a player.assign seats a newer device on the same slot) is pruned here
            // exactly like a camera the seat no longer demands at all.
            var isResolvedDevice = (
                (seat != 0) &&
                m_roster.TryGetSeatDevice(
                slot: PlayerRoster.SlotFromDisplay(number: seat),
                kind: InputDeviceKind.Camera,
                device: out var resolvedDeviceId
            ) &&
                (resolvedDeviceId == device.DeviceId)
            );

            for (var index = (device.Feeds.Count - 1); (index >= 0); index--) {
                var feed = device.Feeds[index];
                var key = (device.DeviceId, feed.Sensor);

                if (
                    !isResolvedDevice ||
                    !m_cameraDemand.TryGetValue(
                    key: (seat, feed.Sensor),
                    value: out var requested
                )
                ) {
                    _ = m_cameraFeeds.Remove(key: key);
                    device.Feeds.RemoveAt(index: index);
                    feed.Dispose();
                    device.SensorsChanged = true;

                    continue;
                }

                if (requested == feed.Profile) {
                    continue;
                }

                var replacement = NewCameraFeed(
                    profile: requested,
                    sensor: feed.Sensor
                );

                feed.Dispose();
                device.Feeds[index] = replacement;
                m_cameraFeeds[key] = replacement;
                device.SensorsChanged = true;
            }
        }
    }
    /// <summary>Acquires the current frame for a previously-declared <see cref="WorldFrameSource"/> — the render-thread,
    /// per-frame counterpart of <see cref="DeclareFrameSource"/>. Reads the same shared feed a screen slot naming the
    /// identical source would (a camera seat/sensor, a named view, a probe id, or a standalone capture), so a HUD
    /// frame and a diegetic screen filming the same source see the same image, and resolves an external source (a
    /// camera, a capture, a probe of either) through the same capture gate a screen does.</summary>
    /// <param name="source">The frame source to sample.</param>
    /// <param name="seat">The 1-based enclosing seat scope a camera source with no authored <c>Seat</c> resolves
    /// against.</param>
    /// <param name="frame">The acquired frame, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the source is live this frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool TryAcquireFrame(WorldFrameSource source, int seat, out GpuImageLease frame) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (
            FillsExternal &&
            ShowsExternal(source: source)
        ) {
            frame = m_fills.Acquire(rgba: ImageSourceDescriptor.DefaultCaptureFill);

            return (0 != frame.ImageViewHandle);
        }

        switch (source) {
            case WorldScreenSource.Producer { Id: WorldImageProducerSettings.CameraId } when WorldImageProducerSettings.TryCamera(
                camera: out var camera,
                source: source
            ):
                if (TryResolveCamera(
                    seat: (camera.Seat ?? seat),
                    sensor: camera.Sensor,
                    device: out _,
                    feed: out var cameraFeed,
                    fault: out _
                )) {
                    frame = cameraFeed!.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.View view:
                // A view's node holds each image it publishes until two of its own submissions after a newer one, so the
                // overlay's frame samples its latest one with no lease, as a consumer instance does.
                if (
                    (Runtime is { } runtime) &&
                    runtime.TryLatestImage(
                        image: out var image,
                        instance: ViewRegistrationName(
                            cameraName: view.CameraName,
                            seat: seat
                        )
                    )
                ) {
                    frame = image.ImageViewHandle;

                    return true;
                }

                break;
            case WorldScreenSource.Probe probe:
                if (m_probeFeeds.TryGetValue(
                    key: probe.Id,
                    value: out var probeFeed
                )) {
                    frame = probeFeed.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
            case WorldScreenSource.Producer { Id: WorldImageProducerSettings.CaptureId }:
                if (m_frameCaptures.TryGetValue(
                    key: source,
                    value: out var captureFeed
                )) {
                    frame = captureFeed.AcquireFrame();

                    return (0 != frame.ImageViewHandle);
                }

                break;
        }

        frame = default;

        return false;
    }

    // Standalone captures ride the same per-frame pull cadence a slot-owned capture does, from Publish (below), and
    // the same device-lost/dispose sweeps every other feed this binder owns gets.
    private void PublishFrameCaptures(in FrameContext context) {
        foreach (var feed in m_frameCaptures.Values) {
            if (feed.ShouldPull()) {
                CaptureWindow(
                    context: in context,
                    feed: feed
                );
            }
        }
    }
}
