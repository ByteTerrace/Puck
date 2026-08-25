using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Render frames between a graph ending (unplug, end of stream) and the reopen attempt, and between a refused open
    // and its retry — long enough for a driver to finish tearing down, short enough that a replug recovers unaided.
    private const int CameraReopenFrames = 60;
    private const int CameraRefusalFrames = 120;
    // Shared-target ring depth per stream: enough room for the renderer's two in-flight frames plus one write target;
    // explicit slot acquisitions enforce the guarantee when producer and renderer cadence diverge.
    private const int CameraTargetCount = 3;
    // How often the device table is refreshed against ICameraCaptureService.EnumerateDevices() (hot plug/unplug) —
    // cheap enough to run every publish, expensive enough (a platform enumeration call) to throttle to a human-scale
    // cadence rather than every produced frame.
    private static readonly long CameraDeviceScanInterval = (2L * Stopwatch.Frequency);

    // The document-member-to-platform-control pairing, stated once so ApplyCameraControlsFor and DescribeCameraFeed
    // can never disagree about which authored member drives which device control.
    private static readonly (CameraControl Control, string Name, Func<WorldCameraControls, int?> Select)[] CameraControlMap = [
        (CameraControl.Pan, "pan", static controls => controls.Pan),
        (CameraControl.Tilt, "tilt", static controls => controls.Tilt),
        (CameraControl.Zoom, "zoom", static controls => controls.Zoom),
        (CameraControl.Exposure, "exposure", static controls => controls.Exposure),
        (CameraControl.Focus, "focus", static controls => controls.Focus),
        (CameraControl.Brightness, "brightness", static controls => controls.Brightness),
        (CameraControl.Contrast, "contrast", static controls => controls.Contrast),
        (CameraControl.Saturation, "saturation", static controls => controls.Saturation),
        (CameraControl.Sharpness, "sharpness", static controls => controls.Sharpness),
        (CameraControl.Gain, "gain", static controls => controls.Gain),
        (CameraControl.WhiteBalance, "whiteBalance", static controls => controls.WhiteBalance),
        (CameraControl.BacklightCompensation, "backlightCompensation", static controls => controls.BacklightCompensation),
        (CameraControl.FieldOfView, "fieldOfView", static controls => controls.FieldOfView),
    ];

    /// <summary>Describes every known camera device — the <c>screen.camera</c> read-back: each device's roster token,
    /// name, sensors, tier, and the seat it is currently assigned to (or <c>unassigned</c>); for a sensor with a live
    /// feed, the same per-sensor detail (negotiated extent, native transport, control surface) the single-camera
    /// read-back always reported. Null when no camera device has ever been enumerated.</summary>
    /// <returns>The description, or <see langword="null"/> when no camera device is known.</returns>
    public string? DescribeCamera() {
        if (0 == m_cameraDeviceOrder.Count) {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var device in m_cameraDeviceOrder) {
            if (builder.Length > 0) {
                _ = builder.Append(value: " | ");
            }

            var token = m_roster.DeviceToken(device: device.DeviceId);
            var seatText = ((m_roster.DeviceSlot(device: device.DeviceId) is { } slot)
                ? $"p{PlayerRoster.DisplayNumber(slot: slot)}"
                : "unassigned"
            );

            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{token} '{device.Name}' [{string.Join(separator: "+", values: device.Sensors)}] {device.Tier} seat={seatText}");

            foreach (var feed in device.Feeds) {
                _ = builder.Append(value: " — ");
                DescribeCameraFeed(builder: builder, device: device, feed: feed);
            }
        }

        return builder.ToString();
    }
    /// <summary>Binds a declared screen to a seat's camera device's sensor — the runtime
    /// <c>screen.source &lt;index&gt; camera [color|infrared] [seat N]</c> path. Any existing producer on the slot is
    /// cleared first. The seat's camera device resolves (and its sensor feed opens, or reopens with the new sensor
    /// set) on the next publish; an unassigned seat or an incompatible sensor then reports through the slot's fault
    /// and <c>screen.camera</c>. Fails loudly for an undeclared screen or a platform without camera support.</summary>
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

        if (!m_slots.TryGetValue(key: index, value: out var slot)) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!m_cameraCapture.IsSupported) {
            return (Ok: false, Message: "no camera device present");
        }

        slot.ClearLive();
        slot.CameraSeat = seat;
        slot.CameraSensorKind = sensor;
        slot.DeclaredFault = null;
        // Demand resolves at the next publish (ReconcileCameraDemand reads CameraSeat/CameraSensorKind directly) —
        // one produced frame's seam between this bind and the seat's device/feed appearing live.

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
        if (
            !TryResolveCamera(seat: seat, sensor: sensor, device: out var device, feed: out var feed, fault: out _) ||
            (feed!.SharedStream is not { } shared)
        ) {
            attachment = default;

            return false;
        }

        attachment = new WorldCameraAttachment(
            Controls: device!.Graph?.Controls,
            Kernels: (device.Shared as ICameraKernelHost),
            Shared: shared,
            TargetSet: feed.GpuTargets
        );

        return true;
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
    // Services the device table, then every known device's lifecycle and every one of its declared feeds. Opens run
    // on the thread pool (a Media Foundation open can block for seconds proving a graph live); the render thread only
    // adopts a finished open.
    private void CaptureCamera(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        ServiceCameraDevices();
        ReconcileCameraDemand();

        foreach (var device in m_cameraDeviceOrder) {
            if (
                (device.Feeds.Count == 0) &&
                (device.Opening is null) &&
                (device.Graph is null)
            ) {
                // A device's graph opens lazily — only once some consumer's resolution lands on it (EnsureCameraFeed
                // declares at least one feed). An empty device with an in-flight open or an adopted graph still needs
                // the lifecycle service below so final-demand removal can dispose it.
                continue;
            }

            ServiceCameraDeviceGraph(deviceContext: deviceContext, device: device);

            foreach (var feed in device.Feeds) {
                ServiceCameraFeed(deviceContext: deviceContext, device: device, feed: feed, gpu: gpu);
            }
        }
    }
    // Re-enumerates every physical camera at boot and roughly every CameraDeviceScanInterval thereafter (hot plug):
    // a newly seen device is content-addressed (InputDeviceId.FromKey — reconnect-stable) and observed onto the
    // roster (its default-seating policy attaches it, or leaves it unassigned); a device that vanished has its graph
    // closed and every feed detached with a "camera disconnected" fault, then drops out of the table so a later
    // reconnect (the SAME content-addressed id) starts fresh. A scan that FAILS (EnumerateDevices throws) is not
    // read as "every camera unplugged" — the device table is left untouched and removals resume only once a scan
    // completes again; the failure narrates once per episode rather than on every retry.
    private void ServiceCameraDevices() {
        var now = Stopwatch.GetTimestamp();

        if (
            (m_nextCameraDeviceScanTimestamp != 0L) &&
            (now < m_nextCameraDeviceScanTimestamp)
        ) {
            return;
        }

        m_nextCameraDeviceScanTimestamp = (now + CameraDeviceScanInterval);

        if (!m_cameraCapture.IsSupported) {
            return;
        }

        CameraDeviceScanOutcome outcome;
        var infoById = new Dictionary<InputDeviceId, CameraDeviceInfo>();

        try {
            foreach (var info in m_cameraCapture.EnumerateDevices()) {
                infoById[InputDeviceId.FromKey(key: info.Id)] = info;
            }

            outcome = new CameraDeviceScanOutcome.Success(Ids: infoById.Keys.ToHashSet());
        } catch (InvalidOperationException exception) {
            outcome = new CameraDeviceScanOutcome.Failure(Message: exception.Message);
        }

        var decision = CameraDeviceScanReconciler.Reconcile(knownIds: m_cameraDevices.Keys.ToHashSet(), outcome: outcome, wasFailing: m_cameraDeviceScanFailed);

        m_cameraDeviceScanFailed = decision.IsFailing;

        if (decision.Narrate) {
            Console.Error.WriteLine(value: $"[camera] device scan failed: {((CameraDeviceScanOutcome.Failure)outcome).Message}");
        }

        if (outcome is CameraDeviceScanOutcome.Failure) {
            return;
        }

        foreach (var (deviceId, info) in infoById) {
            if (m_cameraDevices.TryGetValue(key: deviceId, value: out var device)) {
                device.Name = info.Name;
                device.Sensors = info.Sensors;
            } else {
                device = new CameraDevice(deviceId: deviceId, platformId: info.Id, name: info.Name, sensors: info.Sensors);
                m_cameraDevices[deviceId] = device;
                m_cameraDeviceOrder.Add(item: device);
            }

            m_roster.ObserveDevice(device: deviceId, kind: InputDeviceKind.Camera, name: info.Name);
        }

        foreach (var deviceId in decision.ToRetire) {
            var device = m_cameraDevices[deviceId];

            foreach (var feed in device.Feeds) {
                _ = m_cameraFeeds.Remove(key: (device.DeviceId, feed.Sensor));
            }

            DisposeCameraDevice(device: device, fault: "camera disconnected");
            _ = m_cameraDevices.Remove(key: deviceId);
            _ = m_cameraDeviceOrder.Remove(item: device);
        }
    }
    // Ensures a (seat, sensor) demand entry's feed against the roster's current seating — called from
    // ReconcileCameraFeedsToDemand (WorldScreenBinder.FrameSources.cs) once m_cameraDemand has been rebuilt for the
    // frame. A seat with no enumerated camera, or whose camera lacks this sensor, simply has no feed yet.
    private bool TryFulfillCameraDemand(int seat, WorldCameraSensor sensor) {
        if (
            !m_roster.TryGetSeatDevice(slot: PlayerRoster.SlotFromDisplay(number: seat), kind: InputDeviceKind.Camera, device: out var deviceId) ||
            !m_cameraDevices.TryGetValue(key: deviceId, value: out var device) ||
            !DeviceHasSensor(device: device, sensor: sensor)
        ) {
            return false;
        }

        var profile = (m_cameraDemand.TryGetValue(key: (seat, sensor), value: out var requested) ? requested : WorldFeedProfile.Default);

        _ = EnsureCameraFeed(device: device, sensor: sensor, profile: profile);

        return true;
    }
    /// <summary>Retains one live probe instance's camera feed demand at the socket's authored profile.</summary>
    public void RetainProbeCameraDemand(WorldScreenSource.Camera camera, int contextSeat) =>
        RetainProbeCameraDemandCore(camera: camera, contextSeat: contextSeat);
    /// <summary>Releases one live probe instance's camera feed demand.</summary>
    public void ReleaseProbeCameraDemand(WorldScreenSource.Camera camera, int contextSeat) =>
        ReleaseProbeCameraDemandCore(camera: camera, contextSeat: contextSeat);
    // Resolves (seat, sensor) to the device currently seated there and its feed — the one lookup every camera
    // consumer (a screen slot, a probe socket, a HUD frame) makes each frame. A device already known but with no
    // feed for this sensor yet is ensured here (default profile) so a resolve landing ahead of the next publish's
    // ReconcileCameraDemand (the same produced frame a screen.source camera call binds on) still finds a feed.
    private bool TryResolveCamera(int seat, WorldCameraSensor sensor, out CameraDevice? device, out CameraFeed? feed, out string fault) {
        device = null;
        feed = null;

        if (!m_roster.TryGetSeatDevice(slot: PlayerRoster.SlotFromDisplay(number: seat), kind: InputDeviceKind.Camera, device: out var deviceId)) {
            fault = $"seat {seat} has no camera assigned";

            return false;
        }

        if (!m_cameraDevices.TryGetValue(key: deviceId, value: out device)) {
            fault = $"seat {seat}'s camera is not yet enumerated";

            return false;
        }

        if (!DeviceHasSensor(device: device, sensor: sensor)) {
            fault = $"seat {seat}'s camera '{device.Name}' has no {SensorName(sensor: sensor)} sensor";

            return false;
        }

        if (!m_cameraFeeds.TryGetValue(key: (deviceId, sensor), value: out feed)) {
            feed = EnsureCameraFeed(device: device, sensor: sensor, profile: WorldFeedProfile.Default);
        }

        fault = "";

        return true;
    }
    // The four per-frame reads a ScreenSlot bound to (CameraSeat, CameraSensorKind) makes — thin wrappers over
    // TryResolveCamera so the slot itself carries no camera machinery of its own.
    private SdfScreenSourceFrame AcquireCameraFrame(int seat, WorldCameraSensor sensor) =>
        (TryResolveCamera(seat: seat, sensor: sensor, device: out _, feed: out var feed, fault: out _) ? feed!.AcquireFrame() : default);
    private nint CameraHandleFor(int seat, WorldCameraSensor sensor) =>
        (TryResolveCamera(seat: seat, sensor: sensor, device: out _, feed: out var feed, fault: out _) ? feed!.Handle() : 0);
    private Vector3 CameraLightFor(int seat, WorldCameraSensor sensor) =>
        (TryResolveCamera(seat: seat, sensor: sensor, device: out _, feed: out var feed, fault: out _) ? feed!.Light : Vector3.Zero);
    private string? CameraFaultFor(int seat, WorldCameraSensor sensor) {
        if (!TryResolveCamera(seat: seat, sensor: sensor, device: out _, feed: out var feed, fault: out var fault)) {
            return fault;
        }

        return (feed!.Live ? null : feed.Fault);
    }
    private void ServiceCameraDeviceGraph(CameraDevice device, IGpuDeviceContext deviceContext) {
        // A retired last HUD camera source can leave a physical device with no sensor feeds. Do not reopen an empty
        // graph; if an old profile open was already in flight, dispose its result when it lands instead of adopting it.
        if (device.Feeds.Count == 0) {
            if (device.Opening is { } emptyOpening) {
                if (!emptyOpening.IsCompleted) {
                    return;
                }

                device.Opening = null;

                if (TaskStatus.RanToCompletion == emptyOpening.Status) {
                    DisposeCameraOpenResult(result: emptyOpening.Result);
                } else {
                    _ = emptyOpening.Exception;
                }
            }

            if (device.Graph is not null) {
                CloseCameraGraphFor(device: device, fault: null);
            }

            return;
        }

        if (device.Opening is { } opening) {
            if (!opening.IsCompleted) {
                return;
            }

            device.Opening = null;

            if (device.SensorsChanged) {
                // Demand changed while the platform was opening the previous immutable request set. Never attach
                // that stale profile even for one frame; dispose it and immediately open the current feeds.
                DisposeCameraOpenResult(result: opening.Result);
                BeginCameraOpen(device: device);

                return;
            }

            AdoptCameraGraph(deviceContext: deviceContext, device: device, result: opening.Result);

            return;
        }

        if (device.Graph is { } graph) {
            if (!graph.IsEnded && !device.SensorsChanged) {
                return;
            }

            var sharedStartFailed = (
                graph.IsEnded &&
                (device.Shared is not null) &&
                HasUnpublishedStream(graph: graph)
            );

            // Target handles are attached on the worker after Start returns. If that asynchronous setup ends the
            // graph before every requested stream publishes, refuse the shared tier for exactly the next open so the
            // ladder actually reaches CPU pixels instead of recreating the same failed GPU graph forever.
            if (sharedStartFailed) {
                Console.Error.WriteLine(value: $"[camera] {device.Name}: GPU tier ended before every stream produced a frame; opening the CPU tier.");
                device.SharedRefused = true;
            }

            // A sensor set change and a shared-tier startup refusal reopen at once; a real disconnect after live
            // streaming waits for the driver to settle.
            CloseCameraGraphFor(device: device, fault: (graph.IsEnded ? (sharedStartFailed ? "camera GPU tier refused" : "camera disconnected") : null));
            device.Countdown = ((graph.IsEnded && !sharedStartFailed) ? CameraReopenFrames : 0);
        }

        if (device.Countdown > 0) {
            --device.Countdown;

            return;
        }

        BeginCameraOpen(device: device);
    }
    // The open ladder, off the render thread: shared textures when the render adapter and transport allow, else CPU
    // pixels; when the platform refuses the whole sensor set, the most recently bound sensor is dropped and the
    // remainder retried, down to one.
    private void BeginCameraOpen(CameraDevice device) {
        var feeds = device.Feeds;
        var requests = new CameraStreamRequest[feeds.Count];
        var sensors = new WorldCameraSensor[feeds.Count];

        for (var index = 0; (index < feeds.Count); index++) {
            var feed = feeds[index];

            requests[index] = new CameraStreamRequest(
                Height: feed.Profile.Height,
                RateHz: feed.Profile.RefreshRateHz,
                Sensor: PlatformSensor(sensor: feed.Sensor),
                Width: feed.Profile.Width
            );
            sensors[index] = feed.Sensor;
            feed.Fault = "camera opening";
        }

        var sharedEligible = (
            !device.SharedRefused &&
            (m_hostsOnDirectX || (m_surfaceTransfers is not null)) &&
            OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
        );
        var adapterLuid = (sharedEligible ? m_renderAdapterLuid : null);
        var capture = m_cameraCapture;
        var platformId = device.PlatformId;

        device.SensorsChanged = false;
        device.SharedRefused = false;
        device.Opening = Task.Run(function: () => OpenCamera(adapterLuid: adapterLuid, capture: capture, deviceId: platformId, requests: requests, sensors: sensors));
    }
    private static CameraOpenResult OpenCamera(ICameraCaptureService capture, string deviceId, long? adapterLuid, CameraStreamRequest[] requests, WorldCameraSensor[] sensors) {
        var dropped = new List<WorldCameraSensor>();

        for (var count = requests.Length; (count > 0); count--) {
            var slice = requests.AsSpan(start: 0, length: count);

            if ((adapterLuid is { } luid) && capture.TryOpenShared(adapterLuid: luid, deviceId: deviceId, graph: out var shared, streams: slice)) {
                return new CameraOpenResult(Dropped: [.. dropped], Pixels: null, Shared: shared);
            }

            if (capture.TryOpenPixels(deviceId: deviceId, graph: out var pixels, streams: slice)) {
                return new CameraOpenResult(Dropped: [.. dropped], Pixels: pixels, Shared: null);
            }

            dropped.Add(item: sensors[(count - 1)]);
        }

        return new CameraOpenResult(Dropped: [.. dropped], Pixels: null, Shared: null);
    }
    private void AdoptCameraGraph(CameraDevice device, CameraOpenResult result, IGpuDeviceContext deviceContext) {
        if (result.Shared is { } shared) {
            if (TryProvisionSharedTargets(deviceContext: deviceContext, device: device, fault: out var fault, graph: shared)) {
                device.Shared = shared;
                Console.Out.WriteLine(value: $"[camera] GPU tier: '{shared.Name}' {DescribeStreams(graph: shared)}, {CameraTargetCount} shared targets per sensor{(m_hostsOnDirectX ? "" : ", imported for Vulkan sampling")}.");
            } else {
                // Target provisioning is render-device work the platform cannot foresee; the next attempt, at once,
                // skips the shared tier.
                Console.Error.WriteLine(value: $"[camera] GPU tier start refused ({fault}); opening the CPU tier.");
                shared.Dispose();
                device.SharedRefused = true;

                return;
            }
        } else if (result.Pixels is { } pixels) {
            device.Pixels = pixels;
            Console.Out.WriteLine(value: $"[camera] CPU tier: '{pixels.Name}' {DescribeStreams(graph: pixels)}.");
        } else {
            foreach (var feed in device.Feeds) {
                feed.Detach(fault: CameraAbsenceFault(sensor: feed.Sensor));
            }

            device.Countdown = CameraRefusalFrames;

            return;
        }

        foreach (var sensor in result.Dropped) {
            if (m_cameraFeeds.TryGetValue(key: (device.DeviceId, sensor), value: out var droppedFeed)) {
                droppedFeed.Detach(fault: "the device cannot stream color and infrared concurrently");
            }
        }

        if (device.Graph is { } graph) {
            foreach (var stream in graph.Streams) {
                if (m_cameraFeeds.TryGetValue(key: (device.DeviceId, WorldSensor(sensor: stream.Sensor)), value: out var attaching)) {
                    attaching.Attach(stream: stream);
                }
            }
        }

        device.AppliedControls = null;
    }
    // Allocates each shared stream's ring on the render adapter and starts the stream; a failure releases every ring
    // already provisioned so the graph can be disposed whole.
    private bool TryProvisionSharedTargets(CameraDevice device, ICameraGraph<ICameraSharedStream> graph, IGpuDeviceContext deviceContext, out string fault) {
        if (m_renderAdapterLuid is not { } adapterLuid) {
            fault = "the render adapter reports no LUID";

            return false;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
            fault = "shared camera textures need Windows 10";

            return false;
        }

        var provisioned = new List<CameraFeed>(capacity: graph.Streams.Count);

        foreach (var stream in graph.Streams) {
            if (!m_cameraFeeds.TryGetValue(key: (device.DeviceId, WorldSensor(sensor: stream.Sensor)), value: out var feed)) {
                continue;
            }

            if (!TryProvisionSharedRing(adapterLuid: adapterLuid, deviceContext: deviceContext, fault: out fault, format: stream.TargetFormat, height: stream.Height, images: out var images, importedViews: out var views, imports: out var imports, width: stream.Width)) {
                foreach (var started in provisioned) {
                    started.ReleaseGpuTargets();
                }

                return false;
            }

            var targets = new CameraGpuTargetSet(
                images: images,
                importedViews: views,
                imports: imports,
                ring: stream
            );

            try {
                stream.Start(sharedTargetHandles: targets.SharedHandles);
            } catch (Exception exception) {
                targets.Retire();

                foreach (var started in provisioned) {
                    started.ReleaseGpuTargets();
                }

                fault = exception.Message;

                return false;
            }

            feed.ReleaseGpuTargets();
            feed.GpuTargets = targets;
            provisioned.Add(item: feed);
        }

        fault = "";

        return true;
    }
    // Provisions one shared ring a platform producer (a camera stream, a probe kernel) writes into. Ownership transfers
    // to the caller only on success; every partial D3D12 allocation or Vulkan import is released here on failure. The
    // producer declares its format: the source-reader tier uses BGRA, the coordinated compute tier and every probe
    // output RGBA. All are sampled directly, so no renderer-wide convention leaks.
    [SupportedOSPlatform("windows10.0.10240")]
    private bool TryProvisionSharedRing(long adapterLuid, IGpuDeviceContext deviceContext, SurfaceFormat format, int width, int height, out IReadOnlyList<IGpuExportableStorageImage> images, out IGpuSurfaceImport[]? imports, out nint[]? importedViews, out string fault) {
        var allocated = new IGpuExportableStorageImage[CameraTargetCount];
        var handles = new nint[allocated.Length];
        IGpuSurfaceImport[]? createdImports = null;
        nint[]? createdViews = null;

        try {
            var pixelFormat = (format switch {
                SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
                SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
                _ => throw new NotSupportedException(message: $"shared-target format {format} is unsupported"),
            });
            // The targets are Direct3D 12 shared simultaneous-access textures on both hosts: the D3D12 host samples its
            // own resources, the Vulkan host allocates them on a headless device pinned to the render adapter and
            // imports each handle (one importer per slot).
            var targetContext = (m_hostsOnDirectX
                ? deviceContext
                : (m_cameraTargetDevice ??= new DirectXDeviceContext(
                    adapterLuid: adapterLuid,
                    deviceApi: new DirectXNativeDeviceApi(),
                    minimumFeatureLevel: DirectXFeatureLevel.Level110
                ))
            );
            var export = (m_cameraExport ??= new DirectXGpuSurfaceExportFactory());

            for (var index = 0; (index < allocated.Length); index++) {
                allocated[index] = export.CreateSimultaneousAccessStorageImage(
                    deviceContext: targetContext,
                    format: pixelFormat,
                    height: checked((uint)height),
                    width: checked((uint)width)
                );
                handles[index] = allocated[index].SharedHandle;
            }

            if (!m_hostsOnDirectX) {
                var transfers = (m_surfaceTransfers ?? throw new InvalidOperationException(message: "the Vulkan camera GPU tier needs the surface-transfer factory (absent on a headless boot)"));

                createdImports = new IGpuSurfaceImport[allocated.Length];
                createdViews = new nint[allocated.Length];

                for (var index = 0; (index < allocated.Length); index++) {
                    createdImports[index] = transfers.CreateImport(deviceContext: deviceContext);
                    createdViews[index] = createdImports[index].Import(
                        deviceContext: deviceContext,
                        format: pixelFormat,
                        height: checked((uint)height),
                        sharedHandle: handles[index],
                        width: checked((uint)width)
                    ).ImageViewHandle;
                }
            }

            images = allocated;
            imports = createdImports;
            importedViews = createdViews;
            fault = "";

            return true;
        } catch (Exception exception) {
            if (createdImports is not null) {
                foreach (var import in createdImports) {
                    import?.Dispose();
                }
            }

            foreach (var image in allocated) {
                image?.Dispose();
            }

            images = [];
            imports = null;
            importedViews = null;
            fault = exception.Message;

            return false;
        }
    }
    private void ServiceCameraFeed(CameraDevice device, CameraFeed feed, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (feed.SharedStream is { } shared) {
            // The platform publishes completed slots on its own thread and the screen samples the latest one directly;
            // no CPU pixels ever exist on this tier, so Light stays dark.
            feed.Live = (shared.LatestSlot >= 0);
            feed.Fault = (feed.Live ? null : "camera awaiting a first frame");

            if (feed.Live) {
                ApplyCameraControlsFor(device: device);
            }

            return;
        }

        if ((feed.PixelStream is not { } stream) || !feed.ShouldPull()) {
            return;
        }

        var version = stream.FrameVersion;

        if (version == feed.LastFrameVersion) {
            NoteCameraStarvation(device: device, feed: feed);

            return;
        }

        if (!stream.TryCapture(surface: out var surface)) {
            // The producer advertised a new version but the grab raced it; retry on the next produced frame rather
            // than spending the declaration's whole cadence on the miss.
            feed.Rearm();
            NoteCameraStarvation(device: device, feed: feed);

            return;
        }

        var panelSurface = FitPanelSurface(feed: feed, surface: in surface);

        _ = feed.Surface.Publish(deviceContext: deviceContext, gpu: gpu, surface: in panelSurface);
        feed.StarvedPulls = 0;
        feed.LastFrameVersion = version;
        feed.Live = true;
        feed.Fault = null;
        feed.Light = AverageColor(pixels: panelSurface.Pixels.Span);
        ApplyCameraControlsFor(device: device);
    }
    // Reader construction can succeed while a multiplexing driver delivers only one selected sensor. Count cadence
    // opportunities with no new frame, including a formerly live stream that freezes; after roughly three seconds at
    // the default cadence the no-signal state and its likeliest cause become observable.
    private void NoteCameraStarvation(CameraDevice device, CameraFeed feed) {
        if (++feed.StarvedPulls <= 90) {
            return;
        }

        var siblingLive = false;

        foreach (var other in device.Feeds) {
            siblingLive |= ((other != feed) && other.Live);
        }

        feed.Live = false;
        feed.Fault = (siblingLive
            ? "the device cannot stream color and infrared concurrently"
            : "the camera is not producing frames"
        );
    }
    // Pushes the authored control state onto the open graph's device, per control: a present member sets the control
    // manual at that value (device-clamped), and a member the author removed (present in the last applied state,
    // absent now) restores its driver default — a member never authored never disturbs driver state. Best-effort per
    // control (the device is authoritative; screen.camera reads the result back), and a no-op while the authored state
    // matches what was last applied. Called only once a stream is live: vendor-extension controls (fieldOfView, the
    // vendor rows) are register-accepted but stream-ignored by firmware when written before frames flow. Controls are
    // per DEVICE: whichever seat currently resolves to this device supplies the desired state (the first camera row
    // authoring controls for that seat wins — see ResolveSeatCameraControls).
    private void ApplyCameraControlsFor(CameraDevice device) {
        if (device.Graph is not { } graph) {
            return;
        }

        var desired = (((m_roster.DeviceSlot(device: device.DeviceId) is { } slot) && m_seatCameraControls.TryGetValue(key: PlayerRoster.DisplayNumber(slot: slot), value: out var found))
            ? found
            : null
        );

        if (Equals(objA: desired, objB: device.AppliedControls)) {
            return;
        }

        var surface = graph.Controls;

        foreach (var (control, _, select) in CameraControlMap) {
            var value = ((desired is null) ? null : select(arg: desired));
            var previous = ((device.AppliedControls is null) ? null : select(arg: device.AppliedControls));

            if (value is { } manual) {
                _ = surface.TrySet(control: control, value: manual);
            } else if (previous is not null) {
                _ = surface.TryResetAuto(control: control);
            }
        }

        // Vendor rows last, in authored order — raw byte writes the engine assigns no semantics to, so there is no
        // restore for a removed row (its default is unknowable; authors flip values explicitly).
        if (desired?.Vendor is { } vendorRows) {
            foreach (var row in vendorRows) {
                _ = surface.TryVendorWrite(selector: ((uint)row.Id), value: row.Value);
            }
        }

        device.AppliedControls = desired;
    }
    private void DescribeCameraFeed(StringBuilder builder, CameraDevice device, CameraFeed feed) {
        var sensorName = SensorName(sensor: feed.Sensor);

        if ((feed.Stream is not { } stream) || (device.Graph is not { } graph)) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{sensorName} {device.Tier}{((feed.Fault is { } fault) ? $" '{fault}'" : "")}");

            return;
        }

        var native = stream.NativeFormat;
        var transport = $" (native {native.Subtype}{((native.RateHz > 0.0) ? $"@{native.RateHz.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}" : "")}{((native.Mode is { } mode) ? $"; {mode}" : "")})";

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"{sensorName} {stream.Width}x{stream.Height}{transport}{(feed.Live ? "" : ((feed.Fault is { } liveFault) ? $" '{liveFault}'" : " (no frames)"))}"
        );

        var controls = (((m_roster.DeviceSlot(device: device.DeviceId) is { } slot) && m_seatCameraControls.TryGetValue(key: PlayerRoster.DisplayNumber(slot: slot), value: out var found)) ? found : null);
        var surface = graph.Controls;

        foreach (var (control, label, select) in CameraControlMap) {
            if (!surface.TryGetRange(control: control, range: out var range)) {
                continue;
            }

            _ = surface.TryGet(control: control, value: out var value, auto: out var auto);

            var authored = ((controls is null) ? null : select(arg: controls));

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" — {label} {value}({(auto ? "auto" : "manual")}) [{range.Minimum}..{range.Maximum}]{(range.SupportsAuto ? "+auto" : "")} authored {((authored is { } a) ? a.ToString(provider: CultureInfo.InvariantCulture) : "none")}"
            );
        }

        // The authored vendor rows read back by selector — semantics-free, so the echo is the raw byte pair.
        if (controls?.Vendor is { } vendorRows) {
            foreach (var row in vendorRows) {
                var reads = surface.TryVendorRead(selector: ((uint)row.Id), value: out var current);

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $" — vendor({row.Id}) {(reads ? current.ToString(provider: CultureInfo.InvariantCulture) : "unreadable")} authored {row.Value}"
                );
            }
        }
    }
    // Returns a device's sensor feed, creating it on first demand. A new sensor changes the device's sensor set, so
    // the next publish reopens the graph with it.
    private CameraFeed EnsureCameraFeed(CameraDevice device, WorldCameraSensor sensor, WorldFeedProfile profile) {
        var key = (device.DeviceId, sensor);

        if (m_cameraFeeds.TryGetValue(key: key, value: out var existing)) {
            return existing;
        }

        var feed = new CameraFeed(profile: profile, sensor: sensor, surface: new CpuSurfaceSource()) {
            Fault = "camera opening",
        };

        m_cameraFeeds[key] = feed;
        device.Feeds.Add(item: feed);
        device.SensorsChanged = true;

        return feed;
    }
    // Tears one device's open graph down and detaches every one of its feeds; a fault, when given, is what the feeds
    // report until the next open lands.
    private static void CloseCameraGraphFor(CameraDevice device, string? fault) {
        DisposeOffThread(shared: device.Shared, pixels: device.Pixels);
        device.Shared = null;
        device.Pixels = null;
        device.AppliedControls = null;

        foreach (var feed in device.Feeds) {
            feed.Detach(fault: fault);
        }
    }
    // The final ownership door for a physical device, shared by hot-unplug and binder shutdown. An open already in
    // flight cannot be cancelled through the platform seam, so its successful result is disposed whenever it lands.
    private static void DisposeCameraDevice(CameraDevice device, string? fault) {
        CloseCameraGraphFor(device: device, fault: fault);

        if (device.Opening is { } opening) {
            device.Opening = null;
            _ = opening.ContinueWith(
                continuationAction: static finished => {
                    if (TaskStatus.RanToCompletion == finished.Status) {
                        DisposeCameraOpenResult(result: finished.Result);
                    } else {
                        // OpenCamera normally returns a refusal result; observe an unexpected platform exception so
                        // abandoning a disconnected device cannot surface it later as an unobserved task exception.
                        _ = finished.Exception;
                    }
                },
                cancellationToken: CancellationToken.None,
                continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
                scheduler: TaskScheduler.Default
            );
        }

        foreach (var feed in device.Feeds) {
            feed.Dispose();
        }

        device.Feeds.Clear();
    }
    // A camera graph's Dispose joins its capture worker and shuts the platform session down — hundreds of
    // milliseconds the presentation thread must never spend. The graph is disposable from any thread (an interlocked
    // once-only door), so teardown runs on the pool and the frame that retired the camera proceeds at once.
    private static void DisposeOffThread(ICameraGraph<ICameraSharedStream>? shared, ICameraGraph<ICameraPixelStream>? pixels) {
        if ((shared is null) && (pixels is null)) {
            return;
        }

        _ = Task.Run(action: () => {
            shared?.Dispose();
            pixels?.Dispose();
        });
    }
    private static void DisposeCameraOpenResult(CameraOpenResult result) {
        DisposeOffThread(shared: result.Shared, pixels: result.Pixels);
    }
    // The shared tier's rings are render-device-owned: drop every device's graph with them so the next publish
    // reopens on the live device (a CPU-pixel graph survives device loss untouched). An open in flight adopts against
    // the new device.
    private void CameraDeviceLost() {
        foreach (var device in m_cameraDeviceOrder) {
            if (device.Shared is not null) {
                CloseCameraGraphFor(device: device, fault: null);
                device.Countdown = 0;
            }

            device.SharedRefused = false;

            foreach (var feed in device.Feeds) {
                feed.Surface.NotifyDeviceLost();
                feed.LastFrameVersion = -1L;
                feed.Rearm();
            }
        }
    }
    private void DisposeCamera() {
        foreach (var device in m_cameraDeviceOrder) {
            DisposeCameraDevice(device: device, fault: null);
        }

        m_cameraDevices.Clear();
        m_cameraDeviceOrder.Clear();
        m_cameraFeeds.Clear();
    }

    // The platform session owns its negotiated format and may ignore the preferred extent. A diegetic panel should not
    // upload a megapixel-scale frame it cannot display, so fit CPU pixels into the declaration's envelope before the
    // synchronous GPU upload. The buffer is retained by the feed and reused; no steady-state allocation.
    private static Surface FitPanelSurface(in Surface surface, CameraFeed feed) {
        if (
            !surface.IsCpuPixels ||
            (surface.Width <= feed.OutputWidth) ||
            (surface.Height <= feed.OutputHeight)
        ) {
            return surface;
        }

        const int BytesPerPixel = 4;
        var targetWidth = feed.OutputWidth;
        var targetHeight = feed.OutputHeight;
        var targetByteLength = checked((int)((targetWidth * targetHeight) * BytesPerPixel));

        if ((surface.Pixels.Length < checked((int)((surface.Width * surface.Height) * BytesPerPixel)))) {
            return surface;
        }

        if (
            (feed.PanelPixels is null) ||
            (feed.PanelPixels.Length != targetByteLength)
        ) {
            feed.PanelPixels = GC.AllocateUninitializedArray<byte>(length: targetByteLength);
        }

        var source = MemoryMarshal.Cast<byte, uint>(span: surface.Pixels.Span);
        var target = MemoryMarshal.Cast<byte, uint>(span: feed.PanelPixels.AsSpan());

        for (uint y = 0; (y < targetHeight); y++) {
            var sourceY = ((y * surface.Height) / targetHeight);
            var targetRow = (y * targetWidth);
            var sourceRow = (sourceY * surface.Width);

            for (uint x = 0; (x < targetWidth); x++) {
                var sourceX = ((x * surface.Width) / targetWidth);

                target[checked((int)(targetRow + x))] = source[checked((int)(sourceRow + sourceX))];
            }
        }

        return Surface.CpuPixels(
            pixels: feed.PanelPixels,
            width: targetWidth,
            height: targetHeight,
            format: surface.Format
        );
    }
    private static string CameraAbsenceFault(WorldCameraSensor sensor) => ((WorldCameraSensor.Infrared == sensor)
        ? "no infrared camera present"
        : "no camera device present"
    );
    private static string DescribeStreams<TStream>(ICameraGraph<TStream> graph) where TStream : ICameraStream {
        var parts = new string[graph.Streams.Count];

        for (var index = 0; (index < parts.Length); index++) {
            var stream = graph.Streams[index];

            parts[index] = $"{stream.Sensor.ToString().ToLowerInvariant()} {stream.Width}x{stream.Height}";
        }

        return string.Join(separator: " + ", value: parts);
    }
    private static bool HasUnpublishedStream(ICameraGraph<ICameraStream> graph) {
        foreach (var stream in graph.Streams) {
            if (0 == stream.FrameVersion) {
                return true;
            }
        }

        return false;
    }
    private static CameraSensor PlatformSensor(WorldCameraSensor sensor) => ((WorldCameraSensor.Infrared == sensor)
        ? CameraSensor.Infrared
        : CameraSensor.Color
    );
    // A device's sensor set is a handful of entries at most — a manual scan avoids pulling System.Linq into a file
    // that otherwise carries no allocation on its per-frame resolution path.
    private static bool DeviceHasSensor(CameraDevice device, WorldCameraSensor sensor) {
        var platformSensor = PlatformSensor(sensor: sensor);

        foreach (var candidate in device.Sensors) {
            if (candidate == platformSensor) {
                return true;
            }
        }

        return false;
    }
    // A device carries one control state, so — unlike the profile's richest-envelope merge below — the first
    // declared camera screen authoring controls for a given seat wins (two zoom values have no meaningful merge).
    // Document order is the author's priority statement. Per-seat rather than per-document: two seats' cameras (two
    // physical devices) each carry their own control state.
    private static Dictionary<int, WorldCameraControls?> ResolveSeatCameraControls(IReadOnlyList<WorldScreen> screens) {
        var map = new Dictionary<int, WorldCameraControls?>();

        foreach (var screen in screens) {
            if (screen.Source is WorldScreenSource.Camera camera) {
                var seat = (camera.Seat ?? 1);

                if (!map.ContainsKey(key: seat)) {
                    map[seat] = camera.Controls;
                }
            }
        }

        return map;
    }
    private static string SensorName(WorldCameraSensor sensor) => sensor.ToString().ToLowerInvariant();
    private static WorldCameraSensor WorldSensor(CameraSensor sensor) => ((CameraSensor.Infrared == sensor)
        ? WorldCameraSensor.Infrared
        : WorldCameraSensor.Color
    );

    // One physical camera device: its open graph (on exactly one tier), the open in flight, and its feeds in bind
    // order — the order the open ladder drops sensors in when the platform refuses the set.
    private sealed class CameraDevice(InputDeviceId deviceId, string platformId, string name, IReadOnlyList<CameraSensor> sensors) {
        public WorldCameraControls? AppliedControls { get; set; }
        public int Countdown { get; set; }
        public InputDeviceId DeviceId { get; } = deviceId;
        public List<CameraFeed> Feeds { get; } = [];
        public ICameraGraph<ICameraStream>? Graph => ((ICameraGraph<ICameraStream>?)Shared ?? Pixels);
        public string Name { get; set; } = name;
        public Task<CameraOpenResult>? Opening { get; set; }
        public ICameraGraph<ICameraPixelStream>? Pixels { get; set; }
        public string PlatformId { get; } = platformId;
        public bool SensorsChanged { get; set; }
        public IReadOnlyList<CameraSensor> Sensors { get; set; } = sensors;
        public ICameraGraph<ICameraSharedStream>? Shared { get; set; }
        public bool SharedRefused { get; set; }
        public string Tier => ((Shared is not null)
            ? "gpu"
            : ((Pixels is not null)
                ? "cpu"
                : ((Opening is not null) ? "opening" : "unopened")
            )
        );
    }
    private readonly record struct CameraOpenResult(ICameraGraph<ICameraSharedStream>? Shared, ICameraGraph<ICameraPixelStream>? Pixels, WorldCameraSensor[] Dropped);
    // One sensor's shared feed: its stream on whichever tier the device opened, the render resources that tier needs
    // (shared rings and the Vulkan host's importers, or the CPU upload surface), and live/fault/glow state. The handle
    // is 0 (unbound) until the first frame lands and whenever the feed is not live.
    private sealed class CameraFeed(WorldFeedProfile profile, WorldCameraSensor sensor, CpuSurfaceSource surface) : IDisposable {
        private readonly PullCadence m_cadence = new(rateHz: profile.RefreshRateHz);
        private Action<int>? m_releaseCpuFrame;
        private int m_outstandingCpuFrames;
        private bool m_retired;
        private bool m_surfaceDisposed;

        public string? Fault { get; set; }
        public CameraGpuTargetSet? GpuTargets { get; set; }
        public long LastFrameVersion { get; set; } = -1L;
        public Vector3 Light { get; set; }
        public bool Live { get; set; }
        public uint OutputHeight { get; } = checked((uint)profile.Height);
        public uint OutputWidth { get; } = checked((uint)profile.Width);
        public byte[]? PanelPixels { get; set; }
        public ICameraPixelStream? PixelStream { get; private set; }
        public WorldFeedProfile Profile { get; } = profile;
        public WorldCameraSensor Sensor { get; } = sensor;
        public ICameraSharedStream? SharedStream { get; private set; }
        public int StarvedPulls { get; set; }
        public ICameraStream? Stream => ((ICameraStream?)SharedStream ?? PixelStream);
        public CpuSurfaceSource Surface { get; } = surface;

        public void Attach(ICameraStream stream) {
            SharedStream = (stream as ICameraSharedStream);
            PixelStream = (stream as ICameraPixelStream);
            Fault = null;
            LastFrameVersion = -1L;
            Live = false;
            StarvedPulls = 0;
            m_cadence.Rearm();
        }
        public void Detach(string? fault) {
            SharedStream = null;
            PixelStream = null;
            ReleaseGpuTargets();
            Fault = fault;
            LastFrameVersion = -1L;
            Live = false;
            StarvedPulls = 0;
            m_cadence.Rearm();
        }
        public void Dispose() {
            if (m_retired) {
                return;
            }

            m_retired = true;
            ReleaseGpuTargets();

            if (0 == m_outstandingCpuFrames) {
                DisposeSurface();
            }
        }
        public SdfScreenSourceFrame AcquireFrame() {
            if (!Live || m_retired) {
                return 0;
            }

            if (GpuTargets is { } targets) {
                // The latest completed copy's image view, acquired until the SDF frame that samples it retires. The
                // target set also defers its own destruction across a graph close while any such frame remains live.
                return (targets.TryAcquire(frame: out var frame) ? frame : 0);
            }

            var handle = Surface.CurrentHandle;

            if (0 == handle) {
                return 0;
            }

            m_releaseCpuFrame ??= ReleaseCpuFrame;
            ++m_outstandingCpuFrames;

            return new SdfScreenSourceFrame(
                ImageViewHandle: handle,
                Release: m_releaseCpuFrame
            );
        }
        public nint Handle() {
            if (!Live) {
                return 0;
            }

            if ((SharedStream is { LatestSlot: >= 0 and var slot }) && (GpuTargets is { } targets)) {
                return targets.Handle(slot: slot);
            }

            return Surface.CurrentHandle;
        }
        public void Rearm() => m_cadence.Rearm();
        // Retires the shared ring. Its resources are destroyed immediately when no submitted frame samples them, or
        // by the last frame retirement otherwise.
        public void ReleaseGpuTargets() {
            var targets = GpuTargets;

            GpuTargets = null;
            targets?.Retire();
        }
        public bool ShouldPull() => m_cadence.ShouldPull();
        private void DisposeSurface() {
            if (m_surfaceDisposed) {
                return;
            }

            m_surfaceDisposed = true;
            Surface.Dispose();
        }
        private void ReleaseCpuFrame(int token) {
            _ = token;
            --m_outstandingCpuFrames;

            if (m_retired && (0 == m_outstandingCpuFrames)) {
                DisposeSurface();
            }
        }
    }
    // One platform producer's render-device-owned target ring — a camera stream's or a probe kernel output's. A
    // screen-source frame acquires both the ring slot (so the producer cannot overwrite it) and this set's lifetime
    // (so a producer close cannot destroy the texture while an already-submitted renderer frame still samples it).
    // All methods run on the render thread except the ring's producer-side checks.
    private sealed class CameraGpuTargetSet {
        private readonly IReadOnlyList<IGpuExportableStorageImage> m_images;
        private readonly nint[]? m_importedViews;
        private readonly IGpuSurfaceImport[]? m_imports;
        private readonly Action<int> m_release;
        private readonly nint[] m_sharedHandles;
        private readonly ISharedSlotRing m_stream;

        private bool m_disposed;
        private int m_outstanding;
        private bool m_retired;

        /// <summary>Gets the ring's exportable target images' shared handles, in slot order — fixed for the life of
        /// the set, so a per-frame reader never re-derives them.</summary>
        public IReadOnlyList<nint> SharedHandles => m_sharedHandles;

        public CameraGpuTargetSet(IReadOnlyList<IGpuExportableStorageImage> images, nint[]? importedViews, IGpuSurfaceImport[]? imports, ISharedSlotRing ring) {
            m_images = images;
            m_importedViews = importedViews;
            m_imports = imports;
            m_stream = ring;
            m_release = Release;
            m_sharedHandles = new nint[images.Count];

            for (var index = 0; (index < images.Count); index++) {
                m_sharedHandles[index] = images[index].SharedHandle;
            }
        }

        public void Retire() {
            m_retired = true;

            if (0 == m_outstanding) {
                DisposeResources();
            }
        }
        public nint Handle(int slot) {
            if ((slot < 0) || (slot >= m_images.Count)) {
                return 0;
            }

            return (((m_importedViews is { } views) && (slot < views.Length))
                ? views[slot]
                : m_images[slot].ImageViewHandle
            );
        }
        public bool TryAcquire(out SdfScreenSourceFrame frame) {
            if (m_retired || !m_stream.TryAcquireLatest(slot: out var slot)) {
                frame = default;

                return false;
            }

            if ((slot < 0) || (slot >= m_images.Count)) {
                m_stream.Release(slot: slot);
                frame = default;

                return false;
            }

            ++m_outstanding;

            var handle = Handle(slot: slot);

            frame = new SdfScreenSourceFrame(
                ImageViewHandle: handle,
                Release: m_release,
                ReleaseToken: slot
            );

            return true;
        }

        private void DisposeResources() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;

            if (m_imports is not null) {
                foreach (var import in m_imports) {
                    import.Dispose();
                }
            }

            foreach (var image in m_images) {
                image.Dispose();
            }
        }
        private void Release(int slot) {
            m_stream.Release(slot: slot);
            --m_outstanding;

            if (m_retired && (0 == m_outstanding)) {
                DisposeResources();
            }
        }
    }
}
