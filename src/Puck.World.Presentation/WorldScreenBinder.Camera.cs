using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    /// <summary>Describes every known camera device — the <c>screen.camera</c> read-back: each device's roster token,
    /// name, sensors, tier, and the seat it is currently assigned to (or <c>unassigned</c>); for a sensor with a live
    /// feed, the same per-sensor detail (negotiated extent, native transport, control surface) the single-camera
    /// read-back always reported. Null when no camera device has ever been enumerated.</summary>
    /// <returns>The description, or <see langword="null"/> when no camera device is known.</returns>
    public string? DescribeCamera() => ((m_cameraDeviceOrder.Count == 0)
        ? null
        : DescribeCameraDevices());
    /// <summary>Binds a declared screen to a seat's camera device's sensor — the runtime
    /// <c>screen.source &lt;index&gt; camera [color|infrared] [seat N]</c> path. The screen shows the camera's source
    /// instance over its row from the render graph's next frame. The seat's camera device resolves (and its sensor feed
    /// opens, or reopens with the new sensor set) on the next publish; an unassigned seat or an incompatible sensor then
    /// reports through the screen's fault and <c>screen.camera</c>. Fails loudly for an undeclared screen or a platform
    /// without camera support.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="sensor">Which sensor stream to bind.</param>
    /// <param name="seat">The 1-based local seat whose camera device this screen shows.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCamera(int index, WorldCameraSensor sensor, int seat) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (!Enum.IsDefined(value: sensor)) {
            return (Ok: false, Message: $"unknown camera sensor '{sensor}'");
        }

        if (!m_slots.TryGetValue(
            key: index,
            value: out var slot
        )) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!m_cameraCapture.IsSupported) {
            return (Ok: false, Message: "no camera device present");
        }

        // Demand resolves at the next publish (ReconcileCameraDemand reads the camera each screen shows) — one produced
        // frame's seam between this bind and the seat's device/feed appearing live.
        Rebind(
            live: WorldImageProducerSettings.SourceOf(
                id: WorldImageProducerSettings.CameraId,
                settings: new WorldCameraSettings(
                    Seat: seat,
                    Sensor: sensor
                )
            ),
            slot: slot
        );

        return (Ok: true, Message: $"screen {index} showing seat {seat}'s {SensorName(sensor: sensor)} webcam");
    }
    /// <summary>Reads a seat's camera attachment for the probes host: the shared-tier stream, the graph's kernel
    /// host, and the open device's control surface. A seat with no camera assigned, an incompatible sensor, or no
    /// started shared-tier stream answers <see langword="false"/> with a default attachment — the probes host reads
    /// that as "no camera GPU tier available yet" and records its own fault rather than throwing.</summary>
    /// <param name="seat">The 1-based local seat whose camera device to read.</param>
    /// <param name="sensor">Which physical sensor to read.</param>
    /// <param name="attachment">The live attachment, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a shared-tier stream is open for the seat's sensor.</returns>
    public bool TryGetCameraAttachment(int seat, WorldCameraSensor sensor, out WorldCameraAttachment attachment) {
        attachment = default;

        return (TryResolveCamera(
            device: out var device,
            fault: out _,
            feed: out var feed,
            seat: seat,
            sensor: sensor
        ) && TryAttachCamera(
            attachment: out attachment,
            device: device!,
            feed: feed!
        ));
    }
    /// <summary>The roster token (<c>camera&lt;N&gt;</c>) of the camera device currently mapped to a seat, or
    /// <see langword="null"/> when the seat has no camera assigned — the resolved-device echo <c>probe.status</c>
    /// prints per camera socket.</summary>
    /// <param name="seat">The 1-based local seat.</param>
    public string? ResolvedCameraToken(int seat) => (m_roster.TryGetSeatDevice(
        slot: PlayerRoster.SlotFromDisplay(number: seat),
        kind: InputDeviceKind.Camera,
        device: out var device
    )
        ? m_roster.DeviceToken(device: device)
        : null
    );
    /// <summary>Retains one live probe instance's camera feed demand at the socket's authored profile.</summary>
    public void RetainProbeCameraDemand(WorldCameraSettings camera, int contextSeat) =>
        RetainProbeCameraDemandCore(
            camera: camera,
            contextSeat: contextSeat
        );
    /// <summary>Releases one live probe instance's camera feed demand.</summary>
    public void ReleaseProbeCameraDemand(WorldCameraSettings camera, int contextSeat) =>
        ReleaseProbeCameraDemandCore(
            camera: camera,
            contextSeat: contextSeat
        );

    // Resolves (seat, sensor) to the device currently seated there and its feed — the one lookup every camera
    // consumer (a screen slot, a probe socket, a HUD frame) makes each frame. A device already known but with no
    // feed for this sensor yet is ensured here (default profile) so a resolve landing ahead of the next publish's
    // ReconcileCameraDemand (the same produced frame a screen.source camera call binds on) still finds a feed.
    private bool TryResolveCamera(int seat, WorldCameraSensor sensor, out CameraDevice? device, out CameraFeed? feed, out string fault) {
        if (!m_roster.TryGetSeatDevice(
            slot: PlayerRoster.SlotFromDisplay(number: seat),
            kind: InputDeviceKind.Camera,
            device: out var deviceId
        )) {
            (device, feed, fault) = (null, null, $"seat {seat} has no camera assigned");

            return false;
        }

        return TryResolveSeatedCamera(
            device: out device,
            deviceId: deviceId,
            fault: out fault,
            feed: out feed,
            seat: seat,
            sensor: sensor
        );
    }
    // A device carries one control state, so — unlike the profile's richest-envelope merge below — the first
    // declared camera screen authoring controls for a given seat wins (two zoom values have no meaningful merge).
    // Document order is the author's priority statement. Per-seat rather than per-document: two seats' cameras (two
    // physical devices) each carry their own control state.
    private static Dictionary<int, WorldCameraControls?> ResolveSeatCameraControls(IReadOnlyList<WorldScreen> screens) {
        var map = new Dictionary<int, WorldCameraControls?>();

        foreach (var screen in screens) {
            if (WorldImageProducerSettings.TryCamera(
                camera: out var camera,
                source: screen.Source
            )) {
                var seat = (camera.Seat ?? 1);

                if (!map.ContainsKey(key: seat)) {
                    map[seat] = camera.Controls;
                }
            }
        }

        return map;
    }
    private static string SensorName(WorldCameraSensor sensor) => sensor.ToString().ToLowerInvariant();
}
