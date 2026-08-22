using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Render frames between a graph ending (unplug, end of stream) and the reopen attempt, and between a refused open
    // and its retry — long enough for a driver to finish tearing down, short enough that a replug recovers unaided.
    private const int CameraReopenFrames = 60;
    private const int CameraRefusalFrames = 120;
    // Shared-target ring depth per stream: enough room for the renderer's two in-flight frames plus one write target;
    // explicit slot acquisitions enforce the guarantee when producer and renderer cadence diverge.
    private const int CameraTargetCount = 3;

    // The document-member-to-platform-control pairing, stated once so ApplyCameraControls and DescribeCamera can never
    // disagree about which authored member drives which device control.
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

    /// <summary>Describes the camera device's live state — the <c>screen.camera</c> read-back: per sensor, the device
    /// name, tier, negotiated extent, native transport subtype/rate and coordinated mode, each device-supported
    /// control's current value/mode, device envelope, and the authored document value, plus the authored vendor rows
    /// read back raw. A sensor without a stream reports its fault. Null when no camera feed was ever attempted.</summary>
    /// <returns>The description, or <see langword="null"/> when no camera feed or fault exists.</returns>
    public string? DescribeCamera() {
        if ((0 == m_cameraFeeds.Count) && (0 == m_cameraFaults.Count)) {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var sensor in (WorldCameraSensor[])[WorldCameraSensor.Color, WorldCameraSensor.Infrared]) {
            string? fault = null;

            if (!m_cameraFeeds.TryGetValue(key: sensor, value: out var feed) && !m_cameraFaults.TryGetValue(key: sensor, value: out fault)) {
                continue;
            }

            if (builder.Length > 0) {
                _ = builder.Append(value: " | ");
            }

            if (feed is not null) {
                DescribeCameraFeed(builder: builder, feed: feed);
            } else {
                _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{SensorName(sensor: sensor)} '{fault}'");
            }
        }

        return builder.ToString();
    }
    /// <summary>Binds a declared screen to a sensor's shared live webcam feed — the runtime
    /// <c>screen.source &lt;index&gt; camera [color|infrared]</c> path. Any existing producer on the slot is cleared
    /// first. The device opens (or reopens with the new sensor set) on the next publish; an absent or incompatible
    /// sensor then reports through the slot's fault and <c>screen.camera</c>. Fails loudly for an undeclared screen or a
    /// platform without camera support.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="sensor">Which sensor stream's shared feed to bind.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCamera(int index, WorldCameraSensor sensor) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (!Enum.IsDefined(value: sensor)) {
            return (Ok: false, Message: $"unknown camera sensor '{sensor}'");
        }

        if (!m_slots.TryGetValue(key: index, value: out var slot)) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (EnsureCameraFeed(profile: WorldFeedProfile.Default, sensor: sensor) is not { } feed) {
            return (Ok: false, Message: m_cameraFaults.GetValueOrDefault(key: sensor, defaultValue: "no camera device present"));
        }

        slot.ClearLive();
        slot.Camera = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing the {SensorName(sensor: sensor)} webcam");
    }

    /// <summary>Reads one sensor's live camera attachment for the probes host: the shared-tier stream, the graph's
    /// kernel host, and the open device's control surface. A sensor with no feed or no started shared-tier stream
    /// answers <see langword="false"/> with a default attachment — the probes host reads that as "no camera GPU tier
    /// available yet" and records its own fault rather than throwing.</summary>
    /// <param name="sensor">Which physical sensor to read.</param>
    /// <param name="attachment">The live attachment, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a shared-tier stream is open for the sensor.</returns>
    public bool TryGetCameraAttachment(WorldCameraSensor sensor, out WorldCameraAttachment attachment) {
        if (
            !m_cameraFeeds.TryGetValue(key: sensor, value: out var feed) ||
            (feed.SharedStream is not { } shared)
        ) {
            attachment = default;

            return false;
        }

        attachment = new WorldCameraAttachment(
            Controls: m_camera.Graph?.Controls,
            Kernels: (m_camera.Shared as ICameraKernelHost),
            Shared: shared,
            TargetSet: feed.GpuTargets
        );

        return true;
    }
    // Services the device's lifecycle, then every sensor feed. Opens run on the thread pool (a Media Foundation open
    // can block for seconds proving a graph live); the render thread only adopts a finished open.
    private void CaptureCamera(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (0 == m_camera.Feeds.Count) {
            return;
        }

        ServiceCameraDevice(deviceContext: deviceContext);

        foreach (var feed in m_camera.Feeds) {
            ServiceCameraFeed(deviceContext: deviceContext, feed: feed, gpu: gpu);
        }
    }
    private void ServiceCameraDevice(IGpuDeviceContext deviceContext) {
        var device = m_camera;

        if (device.Opening is { } opening) {
            if (!opening.IsCompleted) {
                return;
            }

            device.Opening = null;
            AdoptCameraGraph(deviceContext: deviceContext, result: opening.Result);

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
                Console.Error.WriteLine(value: "[camera] GPU tier ended before every stream produced a frame; opening the CPU tier.");
                device.SharedRefused = true;
            }

            // A sensor set change and a shared-tier startup refusal reopen at once; a real disconnect after live
            // streaming waits for the driver to settle.
            CloseCameraGraph(fault: (graph.IsEnded ? (sharedStartFailed ? "camera GPU tier refused" : "camera disconnected") : null));
            device.Countdown = ((graph.IsEnded && !sharedStartFailed) ? CameraReopenFrames : 0);
        }

        if (device.Countdown > 0) {
            --device.Countdown;

            return;
        }

        BeginCameraOpen();
    }
    // The open ladder, off the render thread: shared textures when the render adapter and transport allow, else CPU
    // pixels; when the platform refuses the whole sensor set, the most recently bound sensor is dropped and the
    // remainder retried, down to one.
    private void BeginCameraOpen() {
        var device = m_camera;
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

        device.SensorsChanged = false;
        device.SharedRefused = false;
        device.Opening = Task.Run(function: () => OpenCamera(adapterLuid: adapterLuid, capture: capture, requests: requests, sensors: sensors));
    }
    private static CameraOpenResult OpenCamera(ICameraCaptureService capture, long? adapterLuid, CameraStreamRequest[] requests, WorldCameraSensor[] sensors) {
        var dropped = new List<WorldCameraSensor>();

        for (var count = requests.Length; (count > 0); count--) {
            var slice = requests.AsSpan(start: 0, length: count);

            if ((adapterLuid is { } luid) && capture.TryOpenShared(adapterLuid: luid, graph: out var shared, streams: slice)) {
                return new CameraOpenResult(Dropped: [.. dropped], Pixels: null, Shared: shared);
            }

            if (capture.TryOpenPixels(graph: out var pixels, streams: slice)) {
                return new CameraOpenResult(Dropped: [.. dropped], Pixels: pixels, Shared: null);
            }

            dropped.Add(item: sensors[(count - 1)]);
        }

        return new CameraOpenResult(Dropped: [.. dropped], Pixels: null, Shared: null);
    }
    private void AdoptCameraGraph(CameraOpenResult result, IGpuDeviceContext deviceContext) {
        var device = m_camera;

        if (result.Shared is { } shared) {
            if (TryProvisionSharedTargets(deviceContext: deviceContext, fault: out var fault, graph: shared)) {
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
            m_cameraFeeds[sensor].Detach(fault: "the device cannot stream color and infrared concurrently");
        }

        if (device.Graph is { } graph) {
            foreach (var stream in graph.Streams) {
                m_cameraFeeds[WorldSensor(sensor: stream.Sensor)].Attach(stream: stream);
            }
        }

        device.AppliedControls = null;
    }
    // Allocates each shared stream's ring on the render adapter and starts the stream; a failure releases every ring
    // already provisioned so the graph can be disposed whole.
    private bool TryProvisionSharedTargets(ICameraGraph<ICameraSharedStream> graph, IGpuDeviceContext deviceContext, out string fault) {
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
            var feed = m_cameraFeeds[WorldSensor(sensor: stream.Sensor)];

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
    private void ServiceCameraFeed(CameraFeed feed, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (feed.SharedStream is { } shared) {
            // The platform publishes completed slots on its own thread and the screen samples the latest one directly;
            // no CPU pixels ever exist on this tier, so Light stays dark.
            feed.Live = (shared.LatestSlot >= 0);
            feed.Fault = (feed.Live ? null : "camera awaiting a first frame");

            if (feed.Live) {
                ApplyCameraControls();
            }

            return;
        }

        if ((feed.PixelStream is not { } stream) || !feed.ShouldPull()) {
            return;
        }

        var version = stream.FrameVersion;

        if (version == feed.LastFrameVersion) {
            NoteCameraStarvation(feed: feed);

            return;
        }

        if (!stream.TryCapture(surface: out var surface)) {
            // The producer advertised a new version but the grab raced it; retry on the next produced frame rather
            // than spending the declaration's whole cadence on the miss.
            feed.Rearm();
            NoteCameraStarvation(feed: feed);

            return;
        }

        var panelSurface = FitPanelSurface(feed: feed, surface: in surface);

        _ = feed.Surface.Publish(deviceContext: deviceContext, gpu: gpu, surface: in panelSurface);
        feed.StarvedPulls = 0;
        feed.LastFrameVersion = version;
        feed.Live = true;
        feed.Fault = null;
        feed.Light = AverageColor(pixels: panelSurface.Pixels.Span);
        ApplyCameraControls();
    }
    // Reader construction can succeed while a multiplexing driver delivers only one selected sensor. Count cadence
    // opportunities with no new frame, including a formerly live stream that freezes; after roughly three seconds at
    // the default cadence the no-signal state and its likeliest cause become observable.
    private void NoteCameraStarvation(CameraFeed feed) {
        if (++feed.StarvedPulls <= 90) {
            return;
        }

        var siblingLive = false;

        foreach (var other in m_camera.Feeds) {
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
    // vendor rows) are register-accepted but stream-ignored by firmware when written before frames flow.
    private void ApplyCameraControls() {
        var device = m_camera;
        var desired = m_cameraControls;

        if ((device.Graph is not { } graph) || Equals(objA: desired, objB: device.AppliedControls)) {
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
    private void DescribeCameraFeed(StringBuilder builder, CameraFeed feed) {
        var device = m_camera;
        var sensorName = SensorName(sensor: feed.Sensor);

        if ((feed.Stream is not { } stream) || (device.Graph is not { } graph)) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"{sensorName} {device.Tier}{((feed.Fault is { } fault) ? $" '{fault}'" : "")}");

            return;
        }

        var native = stream.NativeFormat;
        var transport = $" (native {native.Subtype}{((native.RateHz > 0.0) ? $"@{native.RateHz.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}" : "")}{((native.Mode is { } mode) ? $"; {mode}" : "")})";

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"{sensorName} '{graph.Name}' {device.Tier} {stream.Width}x{stream.Height}{transport}{(feed.Live ? "" : ((feed.Fault is { } liveFault) ? $" '{liveFault}'" : " (no frames)"))}"
        );

        var controls = m_cameraControls;
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
    // Returns the sensor's shared feed, creating it on first demand. A new sensor changes the device's sensor set, so
    // the next publish reopens the graph with it; a platform without camera support records the fault and returns
    // null.
    private CameraFeed? EnsureCameraFeed(WorldFeedProfile profile, WorldCameraSensor sensor) {
        if (m_cameraFeeds.TryGetValue(key: sensor, value: out var existing)) {
            return existing;
        }

        if (!m_cameraCapture.IsSupported) {
            m_cameraFaults[sensor] = CameraAbsenceFault(sensor: sensor);

            return null;
        }

        var feed = new CameraFeed(profile: profile, sensor: sensor, surface: new CpuSurfaceSource()) {
            Fault = "camera opening",
        };

        m_cameraFeeds[sensor] = feed;
        m_camera.Feeds.Add(item: feed);
        m_camera.SensorsChanged = true;
        _ = m_cameraFaults.Remove(key: sensor);

        return feed;
    }
    // Tears the open graph down and detaches every feed; a fault, when given, is what the feeds report until the
    // next open lands.
    private void CloseCameraGraph(string? fault) {
        var device = m_camera;

        device.Shared?.Dispose();
        device.Shared = null;
        device.Pixels?.Dispose();
        device.Pixels = null;
        device.AppliedControls = null;

        foreach (var feed in device.Feeds) {
            feed.Detach(fault: fault);
        }
    }
    // The shared tier's rings are render-device-owned: drop the graph with them so the next publish reopens on the
    // live device (a CPU-pixel graph survives device loss untouched). An open in flight adopts against the new device.
    private void CameraDeviceLost() {
        var device = m_camera;

        if (device.Shared is not null) {
            CloseCameraGraph(fault: null);
            device.Countdown = 0;
        }

        device.SharedRefused = false;

        foreach (var feed in device.Feeds) {
            feed.Surface.NotifyDeviceLost();
            feed.LastFrameVersion = -1L;
            feed.Rearm();
        }
    }
    private void DisposeCamera() {
        var device = m_camera;

        CloseCameraGraph(fault: null);

        if (device.Opening is { } opening) {
            device.Opening = null;
            _ = opening.ContinueWith(continuationAction: static finished => {
                finished.Result.Shared?.Dispose();
                finished.Result.Pixels?.Dispose();
            }, continuationOptions: TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        foreach (var feed in device.Feeds) {
            feed.Dispose();
        }

        device.Feeds.Clear();
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
    // One physical device carries one control state, so — unlike the profile's richest-envelope merge below — the
    // first declared camera screen authoring controls wins (two zoom values have no meaningful merge). Document order
    // is the author's priority statement.
    private static WorldCameraControls? ResolveSharedCameraControls(IReadOnlyList<WorldScreen> screens) {
        foreach (var screen in screens) {
            if (screen.Source is WorldScreenSource.Camera { Controls: { } controls }) {
                return controls;
            }
        }

        return null;
    }
    // One stream per sensor is shared by every screen naming it. When several camera sources declare different
    // preferences for the same sensor, request the richest combined envelope rather than letting declaration order
    // choose.
    private static WorldFeedProfile ResolveSharedCameraProfile(IReadOnlyList<WorldScreen> screens, WorldCameraSensor sensor) {
        var profile = WorldFeedProfile.Default;
        var found = false;

        foreach (var screen in screens) {
            if (screen.Source is not WorldScreenSource.Camera camera || (sensor != camera.Sensor)) {
                continue;
            }

            profile = (found
                ? new WorldFeedProfile(
                    Width: Math.Max(val1: profile.Width, val2: camera.Profile.Width),
                    Height: Math.Max(val1: profile.Height, val2: camera.Profile.Height),
                    RefreshRateHz: Math.Max(val1: profile.RefreshRateHz, val2: camera.Profile.RefreshRateHz)
                )
                : camera.Profile
            );
            found = true;
        }

        return profile;
    }
    private static string SensorName(WorldCameraSensor sensor) => sensor.ToString().ToLowerInvariant();
    private static WorldCameraSensor WorldSensor(CameraSensor sensor) => ((CameraSensor.Infrared == sensor)
        ? WorldCameraSensor.Infrared
        : WorldCameraSensor.Color
    );

    // The one physical camera: its open graph (on exactly one tier), the open in flight, and the feeds in bind order
    // — the order the open ladder drops sensors in when the platform refuses the set.
    private sealed class CameraDevice {
        public WorldCameraControls? AppliedControls { get; set; }
        public int Countdown { get; set; }
        public List<CameraFeed> Feeds { get; } = [];
        public ICameraGraph<ICameraStream>? Graph => ((ICameraGraph<ICameraStream>?)Shared ?? Pixels);
        public Task<CameraOpenResult>? Opening { get; set; }
        public ICameraGraph<ICameraPixelStream>? Pixels { get; set; }
        public bool SensorsChanged { get; set; }
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
            ReleaseGpuTargets();
            Surface.Dispose();
        }
        public SdfScreenSourceFrame AcquireFrame() {
            if (!Live) {
                return 0;
            }

            if (GpuTargets is { } targets) {
                // The latest completed copy's image view, acquired until the SDF frame that samples it retires. The
                // target set also defers its own destruction across a graph close while any such frame remains live.
                return (targets.TryAcquire(frame: out var frame) ? frame : 0);
            }

            return Surface.CurrentHandle;
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
