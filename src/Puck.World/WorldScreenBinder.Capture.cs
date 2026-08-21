using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Platform;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The render adapter LUID a capture feed opens its platform capture on when the D3D12 GPU transport is active, or
    // null on the Vulkan/CPU path (and until the render device is first seen at publish; declared GPU-route captures
    // defer their open to the first pull, where this has resolved).
    private long? AdapterLuidForOpen() => (m_hostsOnDirectX
        ? m_renderAdapterLuid
        : null
    );
    // The declared-data compositor-capture boot: route by the source selector (a window title or a whole monitor). A
    // resolvable target starts live; a target momentarily absent on a supported platform retains a pending feed that
    // reacquires on publication through the same cadence-gated TryEnsureSource path; an unsupported platform faults. On
    // the D3D12 GPU transport the open is ALWAYS deferred to that pending path (the render adapter LUID the platform
    // capture must open on is not resolvable at construction), so a valid declaration retains a pending feed here.
    private void BootDeclaredCapture(ScreenSlot slot, WorldScreenSource.Capture capture) {
        if (capture.MonitorIndex is { } monitorIndex) {
            if (
                m_hostsOnDirectX &&
                m_windowCapture.IsSupported &&
                (monitorIndex >= 0)
            ) {
                slot.Capture = NewCaptureFeed(
                    title: "",
                    profile: capture.Profile,
                    source: null,
                    monitorIndex: monitorIndex
                );
            } else if (TryOpenMonitorCapture(
                monitorIndex: monitorIndex,
                profile: capture.Profile,
                feed: out var monitorFeed,
                fault: out var monitorFault
            )) {
                slot.Capture = monitorFeed;
            } else if (
                m_windowCapture.IsSupported &&
                (monitorIndex >= 0)
            ) {
                slot.Capture = NewCaptureFeed(
                    title: "",
                    profile: capture.Profile,
                    source: null,
                    monitorIndex: monitorIndex,
                    fault: monitorFault
                );
            } else {
                slot.DeclaredFault = monitorFault;
            }

            return;
        }

        if (
            m_hostsOnDirectX &&
            m_windowCapture.IsSupported &&
            !string.IsNullOrWhiteSpace(value: capture.WindowTitle)
        ) {
            slot.Capture = NewCaptureFeed(
                title: capture.WindowTitle,
                profile: capture.Profile,
                source: null
            );
        } else if (TryOpenCapture(
            title: capture.WindowTitle,
            profile: capture.Profile,
            feed: out var captureFeed,
            fault: out var captureFault
        )) {
            slot.Capture = captureFeed;
        } else if (
            m_windowCapture.IsSupported &&
            !string.IsNullOrWhiteSpace(value: capture.WindowTitle)
        ) {
            slot.Capture = NewCaptureFeed(
                title: capture.WindowTitle,
                profile: capture.Profile,
                source: null,
                fault: captureFault
            );
        } else {
            slot.DeclaredFault = captureFault;
        }
    }
    // Services every live shared webcam feed — one per requested sensor. Whether color and infrared can run together
    // is a device/driver capability proven by the dual-open path. On BOTH hosts a feed rides the GPU-resident zero-copy tier — the platform's decode device
    // converts frames on-GPU and copies them into the three shared textures provisioned here, so the screen samples
    // the latest published slot directly and no frame ever visits host memory — falling back to the CPU-pixel tier
    // exactly once if the GPU open refuses (no adapter LUID, no device, a failed target or import). The CPU tier pulls
    // one frame on the capture cadence and publishes it to the shared surface, refreshing the handle + room glow. A
    // disconnected device drops its feed to unbound + fault on either tier.
    private void CaptureCamera(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (!EnsureCoordinatedDualCamera(deviceContext: deviceContext)) {
            return;
        }

        foreach (var feed in m_cameraFeeds.Values) {
            ServiceCameraFeed(
                deviceContext: deviceContext,
                feed: feed,
                gpu: gpu
            );
        }
    }
    // Owns the all-or-nothing lifecycle of a color+IR pair. A dual feed never degrades into two independent opens:
    // it first tries the native-surface GPU graph, and if any probe/target/import/conversion leg refuses, tears BOTH
    // facades down and restores the proven CPU FaceAuth graph. Device loss likewise rebuilds the pair together.
    private bool EnsureCoordinatedDualCamera(IGpuDeviceContext deviceContext) {
        if (
            !m_cameraFeeds.TryGetValue(key: WorldCameraSensor.Color, value: out var colorFeed) ||
            !m_cameraFeeds.TryGetValue(key: WorldCameraSensor.Infrared, value: out var infraredFeed) ||
            !colorFeed.Dual ||
            !infraredFeed.Dual
        ) {
            return true;
        }

        if (
            (colorFeed.SharedSession is { IsEnded: false }) &&
            (infraredFeed.SharedSession is { IsEnded: false })
        ) {
            return true;
        }

        // GPU eligibility is decided here, once per pump: a pair that can never reach the native-surface tier (no
        // render adapter LUID on a headless boot, an older OS) keeps its CPU graph rather than being routed through
        // ServiceCameraFeed into the single-camera GPU opener.
        if (
            (m_renderAdapterLuid is null) ||
            !OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 19041)
        ) {
            colorFeed.GpuRoute = false;
            infraredFeed.GpuRoute = false;
        }

        // Both feeds still carrying GpuRoute with no facade open is the bind-time CPU pair awaiting its single upgrade
        // attempt (or a render-device loss that disposed both facades while preserving the route). MediaCapture holds
        // exclusive control, so the CPU pair releases the device first; a refusal restores it below.
        var upgrade = (
            colorFeed.GpuRoute &&
            infraredFeed.GpuRoute &&
            (colorFeed.SharedSession is null) &&
            (infraredFeed.SharedSession is null)
        );

        if (
            !upgrade &&
            (colorFeed.Session is { IsEnded: false }) &&
            (infraredFeed.Session is { IsEnded: false })
        ) {
            return true;
        }

        if (
            (colorFeed.SharedSession is not null) ||
            (infraredFeed.SharedSession is not null) ||
            (colorFeed.Session is not null) ||
            (infraredFeed.Session is not null)
        ) {
            colorFeed.ResetSessions();
            infraredFeed.ResetSessions();

            if (!upgrade) {
                // An established dual graph ending is a runtime refusal/disconnect. Reopen through the CPU pair.
                colorFeed.GpuRoute = false;
                infraredFeed.GpuRoute = false;
            }
        }

        if (colorFeed.OpenRetryCountdown > 0) {
            --colorFeed.OpenRetryCountdown;
            return false;
        }

        if (colorFeed.GpuRoute && infraredFeed.GpuRoute) {
            if (
                (m_renderAdapterLuid is { } adapterLuid) &&
                OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 19041) &&
                m_cameraCapture.TryOpenSharedDualDefault(
                    adapterLuid: adapterLuid,
                    colorWidth: colorFeed.Profile.Width,
                    colorHeight: colorFeed.Profile.Height,
                    colorRateHz: colorFeed.Profile.RefreshRateHz,
                    infraredWidth: infraredFeed.Profile.Width,
                    infraredHeight: infraredFeed.Profile.Height,
                    infraredRateHz: infraredFeed.Profile.RefreshRateHz,
                    colorSession: out var colorSession,
                    infraredSession: out var infraredSession
                )
            ) {
                var colorStarted = TryStartCameraGpuSession(
                    adapterLuid: adapterLuid,
                    deviceContext: deviceContext,
                    opened: colorSession,
                    images: out var colorImages,
                    imports: out var colorImports,
                    importedViews: out var colorViews,
                    fault: out var colorFault
                );
                var infraredStarted = false;
                IReadOnlyList<IGpuExportableStorageImage> infraredImages = [];
                IGpuSurfaceImport[]? infraredImports = null;
                nint[]? infraredViews = null;
                var infraredFault = "";

                if (colorStarted) {
                    infraredStarted = TryStartCameraGpuSession(
                        adapterLuid: adapterLuid,
                        deviceContext: deviceContext,
                        opened: infraredSession,
                        images: out infraredImages,
                        imports: out infraredImports,
                        importedViews: out infraredViews,
                        fault: out infraredFault
                    );
                }

                if (colorStarted && infraredStarted) {
                    colorFeed.SharedSession = colorSession;
                    colorFeed.GpuTargets = colorImages;
                    colorFeed.GpuImports = colorImports;
                    colorFeed.GpuImportedViews = colorViews;
                    infraredFeed.SharedSession = infraredSession;
                    infraredFeed.GpuTargets = infraredImages;
                    infraredFeed.GpuImports = infraredImports;
                    infraredFeed.GpuImportedViews = infraredViews;
                    colorFeed.AppliedControls = null;
                    infraredFeed.AppliedControls = null;
                    _ = m_cameraFaults.Remove(key: WorldCameraSensor.Color);
                    _ = m_cameraFaults.Remove(key: WorldCameraSensor.Infrared);
                    Console.Out.WriteLine(value: $"[camera] dual GPU tier: '{colorSession.Name}' color {colorSession.Width}x{colorSession.Height} + infrared {infraredSession.Width}x{infraredSession.Height}, three shared RGBA targets per sensor{(m_hostsOnDirectX ? "" : ", imported for Vulkan sampling")}.");
                    return true;
                }

                // Short-circuiting means only the color resource set may exist here. Release anything the first start
                // committed before refusing the whole coordinated graph.
                if (colorStarted) {
                    DisposeCameraGpuResources(images: colorImages, imports: colorImports);
                }

                Console.Error.WriteLine(value: $"[camera] dual GPU tier start refused ({(colorStarted ? infraredFault : colorFault)}); restoring the CPU dual graph.");
                colorSession.Dispose();
                infraredSession.Dispose();
            }

            colorFeed.GpuRoute = false;
            infraredFeed.GpuRoute = false;
        }

        if (TryEnterDualCamera(
            colorProfile: colorFeed.Profile,
            infraredProfile: infraredFeed.Profile,
            requestedProfile: colorFeed.Profile,
            requestedSensor: WorldCameraSensor.Color
        )) {
            return true;
        }

        const string Fault = "the device did not provide coordinated color and infrared feeds";
        colorFeed.Fault = Fault;
        infraredFeed.Fault = Fault;
        colorFeed.OpenRetryCountdown = 60;
        return false;
    }

    private static void DisposeCameraGpuResources(IReadOnlyList<IGpuExportableStorageImage> images, IGpuSurfaceImport[]? imports) {
        if (imports is not null) {
            foreach (var import in imports) {
                import.Dispose();
            }
        }

        foreach (var image in images) {
            image.Dispose();
        }
    }
    private void ServiceCameraFeed(CameraFeed feed, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (
            feed.GpuRoute &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )
        ) {
            CaptureCameraGpu(
                deviceContext: deviceContext,
                feed: feed
            );

            return;
        }

        if (feed.Session is not { } session) {
            if (feed.OpenRetryCountdown > 0) {
                --feed.OpenRetryCountdown;

                return;
            }

            // A transient exclusive-device refusal (notably while a rejected dual graph is still unwinding) must not
            // strand the feed forever. Retry the CPU open at a bounded render-frame cadence; this also makes a camera
            // plugged in after boot recover without rebuilding the world.
            FallBackToCpuCamera(feed: feed);

            return;
        }

        if (session.IsEnded) {
            session.Dispose();
            feed.Session = null;
            // AppliedControls is session-scoped: a future replacement device must receive the authored state again.
            feed.AppliedControls = null;
            feed.Live = false;
            feed.Fault = "camera disconnected";

            return;
        }

        if (!feed.ShouldPull()) {
            return;
        }

        var version = session.FrameVersion;

        if (version == feed.LastFrameVersion) {
            NoteCameraStarvation(feed: feed);

            return;
        }

        if (session.TryCapture(surface: out var surface)) {
            feed.StarvedPulls = 0;
            var panelSurface = FitPanelSurface(
                feed: feed,
                surface: in surface
            );

            _ = feed.Surface.Publish(
                deviceContext: deviceContext,
                gpu: gpu,
                surface: in panelSurface
            );
            feed.LastFrameVersion = version;
            feed.Live = true;
            feed.Fault = null;
            feed.Light = AverageColor(pixels: panelSurface.Pixels.Span);
            // Controls land only once frames flow (see ApplyCameraControls' live-stream contract); the Equals guard
            // inside makes this a per-pull no-op after the first application.
            ApplyCameraControls(
                desired: m_cameraControls,
                feed: feed
            );
        } else {
            // The async producer advertised a new version but the grab raced it. Do not spend the declaration's whole
            // cadence on that miss; retry on the next produced frame while still avoiding more than one attempt here.
            feed.RetryPull();
            NoteCameraStarvation(feed: feed);
        }
    }
    // StartAsync and source-reader construction can both succeed even when a multiplexing driver delivers only one
    // selected sensor. Count cadence opportunities with no new frame, including a formerly-live stream that freezes;
    // after roughly three seconds at the default cadence the no-signal state and its cause become observable.
    private void NoteCameraStarvation(CameraFeed feed) {
        if (++feed.StarvedPulls <= 90) {
            return;
        }

        feed.Live = false;
        feed.Fault = (SiblingFeedStreams(sensor: feed.Sensor)
            ? "the device cannot stream color and infrared concurrently"
            : "the camera is not producing frames"
        );
    }
    // The GPU-resident camera tier's whole per-frame service, on BOTH hosts. A pending feed (no session yet) completes
    // its open here once the render adapter LUID has resolved: negotiate the platform session, provision the three
    // shared targets at the negotiated extent, and hand their shared handles over for the platform to stream into. The
    // targets are Direct3D 12 shared simultaneous-access textures on both hosts — the sharing triangle every leg of
    // which is proven: D3D12 creates them, the platform's D3D11 decode device opens the NT handles and writes the
    // frames, and the D3D12 render device samples its own resources directly while the Vulkan render device imports
    // each handle (VK_KHR_external_memory_win32, one importer per slot) and samples the imported views. On the D3D12
    // host the targets live on the render device; the Vulkan host allocates them on a lazily created headless device
    // pinned to the render adapter's LUID. A refused or faulted open falls back to the CPU tier exactly once. A live
    // session needs only bookkeeping per frame — the platform publishes completed slots on its own thread and the
    // screen's provider samples the latest one directly. No room glow on this tier: frames never visit host memory, so
    // there are no CPU pixels to average — the tier's one trade against the CPU path.
    [SupportedOSPlatform("windows10.0.10240")]
    private void CaptureCameraGpu(CameraFeed feed, IGpuDeviceContext deviceContext) {
        if (feed.SharedSession is not { } session) {
            // Publish resolves the LUID from the render device before this runs, so still-unresolved here means the
            // driver reports none — cross-API sharing is unavailable and the CPU tier is the honest path.
            if (m_renderAdapterLuid is not { } adapterLuid) {
                FallBackToCpuCamera(feed: feed);

                return;
            }

            if (!m_cameraCapture.TryOpenSharedDefault(
                adapterLuid: adapterLuid,
                requestedWidth: feed.Profile.Width,
                requestedHeight: feed.Profile.Height,
                requestedRateHz: feed.Profile.RefreshRateHz,
                sensor: PlatformSensor(sensor: feed.Sensor),
                session: out var opened
            )) {
                FallBackToCpuCamera(feed: feed);

                return;
            }

            if (!TryStartCameraGpuSession(
                adapterLuid: adapterLuid,
                deviceContext: deviceContext,
                opened: opened,
                images: out var images,
                imports: out var imports,
                importedViews: out var importedViews,
                fault: out var fault
            )) {
                Console.Error.WriteLine(value: $"[camera] GPU tier start failed: {fault}; falling back to the CPU tier.");
                opened.Dispose();
                FallBackToCpuCamera(feed: feed);

                return;
            }

            Console.Out.WriteLine(value: $"[camera] GPU tier: '{opened.Name}' {opened.Width}x{opened.Height}, {images.Count} shared targets on the render adapter{(m_hostsOnDirectX ? "" : ", imported for Vulkan sampling")}.");
            feed.SharedSession = opened;
            feed.GpuTargets = images;
            feed.GpuImports = imports;
            feed.GpuImportedViews = importedViews;
            // The idempotence cache belongs to the control surface, not the feed: this is a newly opened device.
            feed.AppliedControls = null;
            session = opened;
        }

        if (session.IsEnded) {
            if (feed.Dual) {
                // The pair owner at CaptureCamera's head tears both facades down together on the next pump.
                feed.Live = false;
                feed.Fault = "coordinated camera graph ended";
                return;
            }

            // Start only wakes the platform worker; opening the shared handles happens asynchronously. An end before
            // the first published slot is therefore a refused GPU start (including an OpenSharedTexture failure), not
            // a live camera disconnect, and must take the advertised CPU fallback instead of retrying GPU forever.
            var publishedFrame = (session.LatestSlot >= 0);

            session.Dispose();
            feed.SharedSession = null;
            feed.AppliedControls = null;
            feed.ReleaseGpuTargets();
            feed.Live = false;

            if (!publishedFrame) {
                Console.Error.WriteLine(value: "[camera] GPU tier ended before its first shared frame; falling back to the CPU tier.");
                FallBackToCpuCamera(feed: feed);
            } else {
                feed.Fault = "camera disconnected";
            }

            return;
        }

        feed.Live = (session.LatestSlot >= 0);
        feed.Fault = (feed.Live
            ? null
            : "camera awaiting a first frame"
        );

        // Controls land only once frames flow (see ApplyCameraControls' live-stream contract); the Equals guard
        // inside makes this a per-frame no-op after the first application.
        if (feed.Live) {
            ApplyCameraControls(
                desired: m_cameraControls,
                feed: feed
            );
        }
    }
    // Provisions and starts one negotiated shared-camera facade. Ownership transfers to the caller only on success;
    // every partial D3D12 allocation or Vulkan import is released here on failure. The session declares its target
    // format: the source-reader GPU tier uses BGRA, while the FaceAuth native-surface compute tier uses RGBA for its
    // private UAV and same-format shared copy. Both are sampled directly, so no renderer-wide convention leaks out.
    [SupportedOSPlatform("windows10.0.10240")]
    private bool TryStartCameraGpuSession(long adapterLuid, IGpuDeviceContext deviceContext, ICameraSharedCaptureSession opened, out IReadOnlyList<IGpuExportableStorageImage> images, out IGpuSurfaceImport[]? imports, out nint[]? importedViews, out string fault) {
        GpuPixelFormat pixelFormat;
        var allocated = new IGpuExportableStorageImage[3];
        var handles = new nint[allocated.Length];
        IGpuSurfaceImport[]? createdImports = null;
        nint[]? createdViews = null;

        try {
            pixelFormat = (opened.TargetFormat switch {
                SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
                SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
                _ => throw new NotSupportedException(message: $"camera shared-target format {opened.TargetFormat} is unsupported"),
            });
            var targetContext = (m_hostsOnDirectX
                ? deviceContext
                : (m_cameraTargetDevice ??= new DirectXDeviceContext(
                    adapterLuid: adapterLuid,
                    deviceApi: new DirectXNativeDeviceApi(),
                    minimumFeatureLevel: DirectXFeatureLevel.Level110
                ))
            );
            var export = (m_cameraExport ??= new DirectXGpuSurfaceExportFactory());

            for (var index = 0; index < allocated.Length; index++) {
                allocated[index] = export.CreateSimultaneousAccessStorageImage(
                    deviceContext: targetContext,
                    format: pixelFormat,
                    height: checked((uint)opened.Height),
                    width: checked((uint)opened.Width)
                );
                handles[index] = allocated[index].SharedHandle;
            }

            if (!m_hostsOnDirectX) {
                var transfers = (m_surfaceTransfers ?? throw new InvalidOperationException(message: "the Vulkan camera GPU tier needs the surface-transfer factory (absent on a headless boot)"));
                createdImports = new IGpuSurfaceImport[allocated.Length];
                createdViews = new nint[allocated.Length];

                for (var index = 0; index < allocated.Length; index++) {
                    createdImports[index] = transfers.CreateImport(deviceContext: deviceContext);
                    createdViews[index] = createdImports[index].Import(
                        deviceContext: deviceContext,
                        format: pixelFormat,
                        height: checked((uint)opened.Height),
                        sharedHandle: handles[index],
                        width: checked((uint)opened.Width)
                    ).ImageViewHandle;
                }
            }

            opened.Start(sharedTargetHandles: handles);
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

    // Pushes the authored control state onto the live session's device, per control: a PRESENT member sets the control
    // manual at that value (device-clamped), and a member the author REMOVED (present in the last applied state, absent
    // now) restores its driver default — a member never authored never disturbs driver state at all. Best-effort per
    // control (the device is authoritative; screen.camera reads the result back), and a no-op while the authored state
    // matches what was last applied, so the per-frame service path calls this freely. CALLED ONLY WITH A LIVE STREAM:
    // vendor-extension controls (fieldOfView, the vendor rows) are register-accepted but STREAM-IGNORED by firmware
    // when written before frames flow (hardware-verified on the BRIO), so application waits for the feed's first live
    // frame rather than riding the session open.
    private static void ApplyCameraControls(CameraFeed feed, WorldCameraControls? desired) {
        if (((object?)feed.SharedSession ?? feed.Session) is not ICameraControlSurface surface) {
            return;
        }

        if (Equals(objA: desired, objB: feed.AppliedControls)) {
            return;
        }

        foreach (var (control, _, select) in CameraControlMap) {
            var value = ((desired is null) ? null : select(arg: desired));
            var previous = ((feed.AppliedControls is null) ? null : select(arg: feed.AppliedControls));

            if (value is { } manual) {
                _ = surface.TrySet(control: control, value: manual);
            } else if (previous is not null) {
                _ = surface.TryResetAuto(control: control);
            }
        }

        // Vendor rows LAST, in authored order — raw byte writes the engine assigns no semantics to, so there is no
        // restore for a removed row (its default is unknowable; authors flip values explicitly).
        if (desired?.Vendor is { } vendorRows) {
            foreach (var row in vendorRows) {
                _ = surface.TryVendorWrite(selector: ((uint)row.Id), value: row.Value);
            }
        }

        feed.AppliedControls = desired;
    }

    /// <summary>Describes every shared camera feed's live control surface — the <c>screen.camera</c> read-back: per
    /// sensor, the device name, tier, negotiated extent, native transport subtype/rate and coordinated mode when the
    /// platform reports them, each device-supported control's current value/mode, device envelope, and the shared
    /// authored document value, plus the authored vendor rows read back raw. A sensor whose open faulted reports its
    /// fault. Null when no feed was ever attempted.</summary>
    /// <returns>The description, or <see langword="null"/> when no camera feed or fault exists.</returns>
    public string? DescribeCamera() {
        if ((0 == m_cameraFeeds.Count) && (0 == m_cameraFaults.Count)) {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var sensor in (WorldCameraSensor[])[WorldCameraSensor.Color, WorldCameraSensor.Infrared]) {
            if (m_cameraFeeds.TryGetValue(key: sensor, value: out var feed)) {
                if (builder.Length > 0) {
                    _ = builder.Append(value: " | ");
                }

                DescribeCameraFeed(builder: builder, feed: feed);
            } else if (m_cameraFaults.TryGetValue(key: sensor, value: out var fault)) {
                if (builder.Length > 0) {
                    _ = builder.Append(value: " | ");
                }

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{sensor.ToString().ToLowerInvariant()} '{fault}'"
                );
            }
        }

        return builder.ToString();
    }
    private void DescribeCameraFeed(StringBuilder builder, CameraFeed feed) {
        var session = ((object?)feed.SharedSession ?? feed.Session);
        var tier = ((feed.SharedSession is not null)
            ? "gpu"
            : ((feed.Session is not null)
                ? "cpu"
                : (feed.GpuRoute ? "pending" : "unopened")
            )
        );
        var sensorName = feed.Sensor.ToString().ToLowerInvariant();

        if (session is not ICameraControlSurface surface) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{sensorName} {tier}{((feed.Fault is { } pendingFault) ? $" '{pendingFault}'" : "")}"
            );

            return;
        }

        var (name, width, height) = ((feed.SharedSession is { } shared)
            ? (shared.Name, shared.Width, shared.Height)
            : (feed.Session!.Name, feed.Session!.Width, feed.Session!.Height)
        );
        var controls = m_cameraControls;
        var format = ((session as ICameraCaptureDiagnostics)?.CaptureFormat);
        var transport = ((format is { } negotiated)
            ? $" (native {negotiated.Subtype}{((negotiated.RateHz > 0.0) ? $"@{negotiated.RateHz.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}" : "")}{((negotiated.Mode is { } mode) ? $"; {mode}" : "")})"
            : ""
        );

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"{sensorName} '{name}' {tier} {width}x{height}{transport}{(feed.Live ? "" : ((feed.Fault is { } liveFault) ? $" '{liveFault}'" : " (no frames)"))}"
        );

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

    // The GPU camera tier refused (no shared open, or the target provisioning/start faulted): flip the feed to the
    // CPU-pixel tier once, opening the ordinary session with the same profile — or record the fault when the device
    // cannot open at all.
    private void FallBackToCpuCamera(CameraFeed feed) {
        feed.GpuRoute = false;
        // A prior GPU surface may have received the same authored record. The CPU session below is a different control
        // surface and must not inherit that idempotence decision.
        feed.AppliedControls = null;

        if (m_cameraCapture.TryOpenDefault(
            requestedWidth: feed.Profile.Width,
            requestedHeight: feed.Profile.Height,
            requestedRateHz: feed.Profile.RefreshRateHz,
            sensor: PlatformSensor(sensor: feed.Sensor),
            session: out var session
        )) {
            feed.Session = session;
            feed.OpenRetryCountdown = 0;
            feed.Fault = null;
            _ = m_cameraFaults.Remove(key: feed.Sensor);
        } else {
            m_cameraFaults[feed.Sensor] = CameraAbsenceFault(sensor: feed.Sensor);
            feed.Fault = m_cameraFaults[feed.Sensor];
            feed.Live = false;
            feed.OpenRetryCountdown = 120;
        }
    }
    // The document sensor selector's platform spelling, and the fault a device absence reads as — sensor-specific, so
    // an authored infrared row on a machine whose IR interface never enumerated says exactly that.
    private static string CameraAbsenceFault(WorldCameraSensor sensor) => ((WorldCameraSensor.Infrared == sensor)
        ? "no infrared camera present"
        : "no camera device present"
    );
    private static CameraSensor PlatformSensor(WorldCameraSensor sensor) => ((WorldCameraSensor.Infrared == sensor)
        ? CameraSensor.Infrared
        : CameraSensor.Color
    );
    // Samples only already-completed compositor frames. A miss holds the last frame. An ended compositor session is
    // disposed before the binder resolves a replacement target (a returning window with the same title, or a reconnected
    // monitor); reacquisition is World policy rather than a compatibility path in the platform feed. On the D3D12 GPU
    // transport the platform copies GPU-side into shared textures the screen samples directly — the CPU surface is never
    // published, only its divided-cadence readback frames feed the room glow.
    private void CaptureWindow(CaptureFeed feed, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (!feed.TryEnsureSource(adapterLuid: AdapterLuidForOpen())) {
            feed.Live = false;
            feed.Fault = $"{feed.Label} is unavailable";
            // No source to sample: drop the shared images so the next open reallocates and re-attaches from scratch.
            feed.ReleaseGpuTargets();

            return;
        }

        if (
            feed.GpuRoute &&
            (m_surfaceExport is not null) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )
        ) {
            EnsureGpuTargets(
                deviceContext: deviceContext,
                feed: feed
            );

            // The divided-cadence CPU frames the platform still reads back keep the AverageColor glow alive with no
            // full per-frame readback; never publish them (the sampled handle is the GPU slot, not this surface).
            if (
                feed.Source!.TryCapture(surface: out var glowSurface) &&
                glowSurface.IsCpuPixels
            ) {
                feed.Light = AverageColor(pixels: glowSurface.Pixels.Span);
            }

            // Live once the platform has completed its first GPU copy — the same first-frame gate the CPU path uses.
            feed.Live = (feed.Source!.GpuRevision > 0L);
            feed.Fault = (feed.Live
                ? null
                : $"{feed.Label} awaiting a compositor frame"
            );

            return;
        }

        if (feed.Source!.TryCapture(surface: out var surface)) {
            _ = feed.Surface.Publish(
                deviceContext: deviceContext,
                gpu: gpu,
                surface: in surface
            );
            feed.Live = true;
            feed.Fault = null;
            feed.Light = AverageColor(pixels: surface.Pixels.Span);
        } else if (!feed.Live) {
            feed.Fault = $"{feed.Label} awaiting a compositor frame";
        }
    }
    // Opens (once per sensor) and returns that sensor's shared webcam feed, or null when its device cannot be opened
    // (m_cameraFaults holds the reason). Every camera screen naming the sensor shares the one feed; a second sensor
    // upgrades to a single dual-stream graph only when both streams prove live. On a modern Windows
    // host — EITHER backend — a feed starts PENDING on the GPU-resident tier (the render adapter LUID it must open on
    // resolves at first publish, so CaptureCameraGpu completes the open and falls back to the CPU tier if it refuses)
    // — the bind succeeds now and a device absence surfaces as the feed's own fault. Elsewhere the CPU-pixel session
    // opens here, synchronously.
    private CameraFeed? EnsureCameraFeed(WorldFeedProfile profile, WorldCameraSensor sensor) {
        if (m_cameraFeeds.TryGetValue(key: sensor, value: out var existing)) {
            return existing;
        }

        if (m_cameraFaults.ContainsKey(key: sensor)) {
            return null;
        }

        if (!m_cameraCapture.IsSupported) {
            m_cameraFaults[sensor] = CameraAbsenceFault(sensor: sensor);

            return null;
        }

        // The OTHER sensor already streams: a multiplexing device cannot run as two unrelated sessions, so entering
        // two-sensor operation rebuilds BOTH feeds onto the platform's coordinated graph. The platform admits it only
        // when a public driver profile sustains both streams and proves both live. The feeds keep their object identity;
        // only their sessions swap.
        var other = ((WorldCameraSensor.Color == sensor) ? WorldCameraSensor.Infrared : WorldCameraSensor.Color);

        if (m_cameraFeeds.TryGetValue(key: other, value: out var otherFeed)) {
            var colorProfile = ((WorldCameraSensor.Color == sensor) ? profile : otherFeed.Profile);
            var infraredProfile = ((WorldCameraSensor.Infrared == sensor) ? profile : otherFeed.Profile);
            // MediaCapture requests exclusive control. A live single-sensor session must release the device before a
            // coordinated upgrade can be attempted; if it is refused, leave the established feed pending so the normal
            // service path reopens it on the next publish.
            if ((otherFeed.Session is not null) || (otherFeed.SharedSession is not null)) {
                otherFeed.ResetSessions();
                otherFeed.GpuRoute = OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240);
            }

            // Keep the GPU and CPU dual opens independent. A modern device may expose a valid native-surface FaceAuth
            // graph even when its CPU-memory graph refuses; first publish tries the GPU pair, and only that refusal
            // opens the CPU fallback. Older Windows has no shared-dual tier and proves the CPU graph here.
            if (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 19041)) {
                var requestedFeed = GetOrCreateCameraFeed(profile: profile, sensor: sensor);
                otherFeed.Dual = true;
                requestedFeed.Dual = true;
                otherFeed.GpuRoute = true;
                requestedFeed.GpuRoute = true;
                _ = m_cameraFaults.Remove(key: sensor);

                return requestedFeed;
            }

            if (!TryEnterDualCamera(colorProfile: colorProfile, infraredProfile: infraredProfile, requestedProfile: profile, requestedSensor: sensor)) {
                m_cameraFaults[sensor] = "the device did not provide coordinated color and infrared feeds";

                return null;
            }

            return m_cameraFeeds[sensor];
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            var pending = new CameraFeed(
                profile: profile,
                sensor: sensor,
                surface: new CpuSurfaceSource(),
                gpuRoute: true
            );

            m_cameraFeeds[sensor] = pending;

            return pending;
        }

        if (!m_cameraCapture.TryOpenDefault(
            requestedWidth: profile.Width,
            requestedHeight: profile.Height,
            requestedRateHz: profile.RefreshRateHz,
            sensor: PlatformSensor(sensor: sensor),
            session: out var session
        )) {
            m_cameraFaults[sensor] = CameraAbsenceFault(sensor: sensor);

            return null;
        }

        var feed = new CameraFeed(
            profile: profile,
            sensor: sensor,
            surface: new CpuSurfaceSource(),
            gpuRoute: false
        ) {
            Session = session,
        };

        m_cameraFeeds[sensor] = feed;

        return feed;
    }
    private bool SiblingFeedStreams(WorldCameraSensor sensor) {
        var sibling = ((WorldCameraSensor.Color == sensor) ? WorldCameraSensor.Infrared : WorldCameraSensor.Color);

        return (m_cameraFeeds.TryGetValue(key: sibling, value: out var feed) && feed.Live);
    }
    // Rebuilds both sensors' feeds onto the platform's coordinated dual session (see EnsureCameraFeed's two-sensor
    // note). Sessions swap IN PLACE on the existing feed objects — slots hold feed references, so a bound screen follows
    // its feed across the swap; the requested sensor's feed is created here when it did not exist yet.
    private bool TryEnterDualCamera(WorldFeedProfile colorProfile, WorldFeedProfile infraredProfile, WorldFeedProfile requestedProfile, WorldCameraSensor requestedSensor) {
        if (!m_cameraCapture.TryOpenDualDefault(
            colorWidth: colorProfile.Width,
            colorHeight: colorProfile.Height,
            colorRateHz: colorProfile.RefreshRateHz,
            infraredWidth: infraredProfile.Width,
            infraredHeight: infraredProfile.Height,
            infraredRateHz: infraredProfile.RefreshRateHz,
            colorSession: out var colorSession,
            infraredSession: out var infraredSession
        )) {
            return false;
        }

        var colorFeed = GetOrCreateCameraFeed(profile: ((WorldCameraSensor.Color == requestedSensor) ? requestedProfile : colorProfile), sensor: WorldCameraSensor.Color);
        var infraredFeed = GetOrCreateCameraFeed(profile: ((WorldCameraSensor.Infrared == requestedSensor) ? requestedProfile : infraredProfile), sensor: WorldCameraSensor.Infrared);

        colorFeed.ResetSessions();
        infraredFeed.ResetSessions();
        colorFeed.GpuRoute = false;
        infraredFeed.GpuRoute = false;
        colorFeed.Dual = true;
        infraredFeed.Dual = true;
        colorFeed.Session = colorSession;
        infraredFeed.Session = infraredSession;
        _ = m_cameraFaults.Remove(key: WorldCameraSensor.Color);
        _ = m_cameraFaults.Remove(key: WorldCameraSensor.Infrared);
        Console.Out.WriteLine(value: $"[camera] dual-sensor session: '{colorSession.Name}' color {colorSession.Width}x{colorSession.Height} + infrared {infraredSession.Width}x{infraredSession.Height} on one coordinated graph.");

        return true;
    }
    private CameraFeed GetOrCreateCameraFeed(WorldFeedProfile profile, WorldCameraSensor sensor) {
        if (m_cameraFeeds.TryGetValue(key: sensor, value: out var existing)) {
            return existing;
        }

        var feed = new CameraFeed(
            profile: profile,
            sensor: sensor,
            surface: new CpuSurfaceSource(),
            gpuRoute: false
        );

        m_cameraFeeds[sensor] = feed;

        return feed;
    }
    // Ensures the feed's THREE simultaneous-access shared textures exist and are attached to its current source at the
    // source's native extent (the sampler scales, so no GPU-side resize is needed). Reallocates on a resize
    // (GpuTargetsOutdated) or a reacquired source; AttachGpuTargets replaces first, then the superseded images are
    // disposed. Cadence-gated by the caller, so it never runs per render frame.
    [SupportedOSPlatform("windows10.0.10240")]
    private void EnsureGpuTargets(CaptureFeed feed, IGpuDeviceContext deviceContext) {
        var source = feed.Source!;
        var width = source.SourceWidth;
        var height = source.SourceHeight;

        // The source has not reported its extent yet (no first frame); nothing to allocate against.
        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            return;
        }

        if (
            (feed.GpuTargets is not null) &&
            !source.GpuTargetsOutdated &&
            ReferenceEquals(
            objA: feed.GpuAttachedSource,
            objB: source
        )
        ) {
            return;
        }

        var images = new IGpuExportableStorageImage[3];
        var handles = new nint[images.Length];

        for (var i = 0; (i < images.Length); ++i) {
            images[i] = m_surfaceExport!.CreateSimultaneousAccessStorageImage(
                deviceContext: deviceContext,
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: ((uint)height),
                width: ((uint)width)
            );
            handles[i] = images[i].SharedHandle;
        }

        var superseded = feed.GpuTargets;

        // Attach first (the platform contract: attach swaps the targets in safely), then release the old allocation.
        source.AttachGpuTargets(targets: new NativeImageGpuCaptureTargets(
            SharedTargetHandles: handles,
            Width: width,
            Height: height
        ));
        feed.GpuTargets = images;
        feed.GpuAttachedSource = source;

        if (superseded is not null) {
            foreach (var image in superseded) {
                image.Dispose();
            }
        }
    }
    // The Media Foundation session owns its negotiated format and may ignore the preferred extent. A diegetic panel
    // should not upload a megapixel-scale frame it cannot display, so fit CPU pixels into the declaration's envelope
    // before the synchronous GPU upload. The buffer is retained by the feed and reused; no steady-state allocation.
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
    // Constructs a capture feed carrying this binder's transport choice (GPU on the D3D12 host, CPU on Vulkan). The one
    // place window/monitor CaptureFeeds are built, so the route flag can never diverge across the open/pending sites.
    private CaptureFeed NewCaptureFeed(string title, WorldFeedProfile profile, INativeImageCaptureFeed? source, int? monitorIndex = null, string? fault = null) =>
        new(
            title: title,
            service: m_windowCapture,
            profile: profile,
            source: source,
            surface: new CpuSurfaceSource(),
            gpuRoute: m_hostsOnDirectX,
            monitorIndex: monitorIndex
        ) {
            Fault = fault,
        };
    // One physical device carries ONE control state, so — unlike the profile's richest-envelope merge below — the
    // FIRST declared camera screen authoring controls wins (two zoom values have no meaningful merge). Document order
    // is the author's priority statement.
    private static WorldCameraControls? ResolveSharedCameraControls(IReadOnlyList<WorldScreen> screens) {
        foreach (var screen in screens) {
            if (screen.Source is WorldScreenSource.Camera { Controls: { } controls }) {
                return controls;
            }
        }

        return null;
    }
    // One physical session per SENSOR is shared to avoid device flicker. When several camera sources declare different
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
                    Width: Math.Max(
                        val1: profile.Width,
                        val2: camera.Profile.Width
                    ),
                    Height: Math.Max(
                        val1: profile.Height,
                        val2: camera.Profile.Height
                    ),
                    RefreshRateHz: Math.Max(
                        val1: profile.RefreshRateHz,
                        val2: camera.Profile.RefreshRateHz
                    )
                )
                : camera.Profile
            );
            found = true;
        }

        return profile;
    }
    // Resolves a live window by title and opens one compositor-owned, self-pumping feed at the declared budget. On the
    // D3D12 GPU transport the platform capture opens on the render adapter (AdapterLuidForOpen) so its shared textures
    // import cross-API.
    private bool TryOpenCapture(string title, WorldFeedProfile profile, out CaptureFeed feed, out string fault) {
        if (string.IsNullOrWhiteSpace(value: title)) {
            feed = null!;
            fault = "a window title is required";

            return false;
        }

        if (
            !m_windowCapture.IsSupported ||
            !m_windowCapture.TryCreateWindowCapture(
            windowTitleFragment: title,
            width: profile.Width,
            height: profile.Height,
            refreshRateHz: profile.RefreshRateHz,
            feed: out var source,
            adapterLuid: AdapterLuidForOpen()
        )
        ) {
            feed = null!;
            fault = $"window capture unavailable for '{title}'";

            return false;
        }

        feed = NewCaptureFeed(
            title: title,
            profile: profile,
            source: source
        );
        fault = "";

        return true;
    }
    // Resolves a whole monitor by 0-based index (0 = primary) and opens one compositor-owned, self-pumping feed at the
    // declared budget. A negative index or a monitor not present faults loudly ("monitor 2 not found"). On the D3D12 GPU
    // transport the platform capture opens on the render adapter (AdapterLuidForOpen).
    private bool TryOpenMonitorCapture(int monitorIndex, WorldFeedProfile profile, out CaptureFeed feed, out string fault) {
        if (monitorIndex < 0) {
            feed = null!;
            fault = $"monitor {monitorIndex} is not a valid index";

            return false;
        }

        if (
            !m_windowCapture.IsSupported ||
            !m_windowCapture.TryCreateMonitorCapture(
            monitorIndex: monitorIndex,
            width: profile.Width,
            height: profile.Height,
            refreshRateHz: profile.RefreshRateHz,
            feed: out var source,
            adapterLuid: AdapterLuidForOpen()
        )
        ) {
            feed = null!;
            fault = $"monitor {monitorIndex} not found";

            return false;
        }

        feed = NewCaptureFeed(
            title: "",
            profile: profile,
            source: source,
            monitorIndex: monitorIndex
        );
        fault = "";

        return true;
    }

    /// <summary>Binds a declared screen to a sensor's shared live webcam feed — the runtime
    /// <c>screen.source &lt;index&gt; camera [color|infrared]</c> path. Any existing producer on the slot is cleared
    /// first. Fails loudly for an undeclared screen or when the sensor's device cannot be opened.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="sensor">Which sensor stream's shared feed to bind. Concurrent color and infrared depend on the
    /// capture device and driver.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCamera(int index, WorldCameraSensor sensor) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (!Enum.IsDefined(value: sensor)) {
            return (Ok: false, Message: $"unknown camera sensor '{sensor}'");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (EnsureCameraFeed(profile: WorldFeedProfile.Default, sensor: sensor) is not { } feed) {
            return (Ok: false, Message: m_cameraFaults.GetValueOrDefault(
                key: sensor,
                defaultValue: "no camera device present"
            ));
        }

        slot.ClearLive();
        slot.Camera = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing the {sensor.ToString().ToLowerInvariant()} webcam");
    }
    /// <summary>Binds a declared screen to a live desktop-window capture keyed by a title fragment — the runtime
    /// <c>screen.source &lt;index&gt; capture</c> path. Any existing producer on the slot is cleared first. The capture rebinds each grab, so
    /// the target window need not be open yet (it reads no signal until it appears, and rebinds if it disappears and
    /// returns); only an unopenable capture service fails here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="windowTitle">The captured window's title fragment (case-insensitive substring match).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCapture(int index, string windowTitle) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryOpenCapture(
            title: windowTitle,
            profile: WorldFeedProfile.Default,
            feed: out var feed,
            fault: out var fault
        )) {
            return (Ok: false, Message: fault);
        }

        slot.ClearLive();
        slot.Capture = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} capturing '{windowTitle}'");
    }
    /// <summary>Binds a declared screen to a live whole-monitor capture keyed by index — the runtime <c>screen.source &lt;index&gt; desktop</c>
    /// path. Any existing producer on the slot is cleared first. The capture rebinds each grab, so it reads no signal
    /// until the monitor is present and reacquires if it disconnects and returns; an out-of-range index or an unopenable
    /// capture service fails here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="monitorIndex">The 0-based monitor to capture whole (0 = primary).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryDesktop(int index, int monitorIndex) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryOpenMonitorCapture(
            monitorIndex: monitorIndex,
            profile: WorldFeedProfile.Default,
            feed: out var feed,
            fault: out var fault
        )) {
            return (Ok: false, Message: fault);
        }

        slot.ClearLive();
        slot.Capture = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} capturing monitor {monitorIndex}");
    }

    // One feed's PRESENTATION CLOCK, stated once so a webcam and a window capture cannot drift into different refresh
    // policies. Camera pixels are nondeterministic presentation input and must not freeze when authoritative simulation
    // time is paused or absent. The first pull after arming always runs; later pulls wait out the profile's whole period.
    private sealed class PullCadence(uint rateHz) {
        private readonly long m_cadenceTicks = Math.Max(
            val1: 1L,
            val2: (Stopwatch.Frequency / Math.Max(val1: rateHz, val2: 1u))
        );

        private long m_lastPullTicks;
        private bool m_pulled;

        public void Rearm() => m_pulled = false;
        public bool ShouldPull() {
            var now = Stopwatch.GetTimestamp();

            if (
                m_pulled &&
                ((now - m_lastPullTicks) < m_cadenceTicks)
            ) {
                return false;
            }

            m_pulled = true;
            m_lastPullTicks = now;

            return true;
        }
    }
    // The ONE shared webcam feed, on one of two tiers fixed by FallBackToCpuCamera at most once: the D3D12 GPU-resident
    // tier (SharedSession + GpuTargets — the screen samples the latest published slot's image view directly, and no CPU
    // pixels ever exist, so Light stays dark) or the CPU-pixel tier (Session + Surface — frames read back to host
    // memory, fitted, uploaded, and averaged into the room glow). A mutable class so the sessions flip in place; the
    // handle is 0 (unbound) until the first frame lands and whenever the feed is not live.
    private sealed class CameraFeed(WorldFeedProfile profile, WorldCameraSensor sensor, CpuSurfaceSource surface, bool gpuRoute) : IDisposable {
        // The control state last pushed onto the device (ApplyCameraControls' idempotence + removed-member baseline);
        // null until a first apply, so an unauthored world never touches driver defaults.
        public WorldCameraControls? AppliedControls { get; set; }
        // Both sensor feeds belong to one driver-declared FaceAuth graph. Their sessions and GPU resources are opened,
        // failed over, and torn down as a pair; neither may silently reopen as an unrelated single-camera session.
        public bool Dual { get; set; }
        public string? Fault { get; set; }
        // Render-frame countdown before retrying a transient CPU open failure. This prevents a busy device from being
        // reopened every frame while still allowing driver teardown and hot-plug recovery without a world rebuild.
        public int OpenRetryCountdown { get; set; }
        // Which physical sensor this feed's sessions open — fixed at construction; a document sensor flip tears the
        // whole feed down and reopens (ReconcileScreens), never re-aims a live session.
        public WorldCameraSensor Sensor { get; } = sensor;
        // Consecutive frame-less pulls on an open session — the starvation detector behind the "cannot stream
        // concurrently" fault (reset the moment any frame arrives).
        public int StarvedPulls { get; set; }
        // Whether this feed rides (or is still pending on) the GPU-resident tier. Set at construction on any modern
        // Windows host; flipped to false exactly once if the GPU open refuses and the feed falls back to the CPU tier.
        public bool GpuRoute { get; set; } = gpuRoute;
        // The Vulkan host's per-slot importers over the shared targets and the imported VkImageView handles the
        // screen samples (null on the D3D12 host, which samples its own resources directly).
        public IGpuSurfaceImport[]? GpuImports { get; set; }
        public nint[]? GpuImportedViews { get; set; }
        // The three consumer-provisioned shared textures the platform streams into (null until the GPU open completes).
        public IReadOnlyList<IGpuExportableStorageImage>? GpuTargets { get; set; }
        public Vector3 Light { get; set; }
        public bool Live { get; set; }
        public byte[]? PanelPixels { get; set; }

        public long LastFrameVersion { get; set; } = -1L;
        public uint OutputHeight { get; } = checked((uint)profile.Height);
        public uint OutputWidth { get; } = checked((uint)profile.Width);
        // The requested capture envelope, stashed so the GPU tier's deferred open (and its CPU fallback) negotiate the
        // same profile the feed was bound with.
        public WorldFeedProfile Profile { get; } = profile;
        public ICameraCaptureSession? Session { get; set; }
        public ICameraSharedCaptureSession? SharedSession { get; set; }
        public CpuSurfaceSource Surface { get; } = surface;

        private PullCadence Cadence { get; } = new(rateHz: profile.RefreshRateHz);

        public void Dispose() {
            Session?.Dispose();
            Session = null;
            SharedSession?.Dispose();
            SharedSession = null;
            ReleaseGpuTargets();
            Surface.Dispose();
        }
        public nint Handle() {
            if (!Live) {
                return 0;
            }

            if (
                (SharedSession is { LatestSlot: >= 0 and var slot }) &&
                (GpuTargets is { } targets) &&
                (slot < targets.Count)
            ) {
                // The image-view of the platform's latest completed GPU copy — the Vulkan host's imported view of
                // the slot, or the D3D12 host's own resource view; the engine rebinds a bound screen source's
                // descriptor every frame, so a different slot handle per copy is cheap.
                return (((GpuImportedViews is { } views) && (slot < views.Length))
                    ? views[slot]
                    : targets[slot].ImageViewHandle
                );
            }

            return Surface.CurrentHandle;
        }
        public void NotifyDeviceLost() {
            Surface.NotifyDeviceLost();

            // GPU-tier resources are render-device-owned: drop the session with its targets so the pull path reopens
            // both on the live device (the CPU-tier Media Foundation session survives device loss untouched).
            if (SharedSession is not null) {
                SharedSession.Dispose();
                SharedSession = null;
                AppliedControls = null;
                Live = false;
            }

            ReleaseGpuTargets();
            LastFrameVersion = -1L;
            Cadence.Rearm();
        }
        // Tears both tiers' sessions down while the feed object (and every slot referencing it) stays — the dual-
        // session upgrade swaps a live feed's sessions in place.
        public void ResetSessions() {
            Session?.Dispose();
            Session = null;
            SharedSession?.Dispose();
            SharedSession = null;
            ReleaseGpuTargets();
            AppliedControls = null;
            LastFrameVersion = -1L;
            Live = false;
            Fault = null;
            OpenRetryCountdown = 0;
            StarvedPulls = 0;
            Cadence.Rearm();
        }
        // Disposes the shared textures and the Vulkan host's importers over them (all device-owned) and forgets both
        // so the next GPU open reallocates on the live device. Called on a lost/ended session, on device loss, and on
        // disposal. The importers go first — an importer's image binds the target's exported memory.
        public void ReleaseGpuTargets() {
            if (GpuImports is { } imports) {
                GpuImports = null;
                GpuImportedViews = null;

                foreach (var import in imports) {
                    import.Dispose();
                }
            }

            if (GpuTargets is not { } targets) {
                return;
            }

            GpuTargets = null;

            foreach (var image in targets) {
                image.Dispose();
            }
        }
        public void RetryPull() => Cadence.Rearm();
        public bool ShouldPull() => Cadence.ShouldPull();
    }
    // One compositor-capture feed: a producer (a desktop window by title, or a whole monitor by index), its GPU upload
    // adapter, and live/fault/glow state. MonitorIndex null is window mode; non-null is whole-monitor mode.
    private sealed class CaptureFeed(
        string title,
        INativeImageCaptureService service,
        WorldFeedProfile profile,
        INativeImageCaptureFeed? source,
        CpuSurfaceSource surface,
        bool gpuRoute = false,
        int? monitorIndex = null
    ) : IDisposable {
        public string? Fault { get; set; }
        public INativeImageCaptureFeed? GpuAttachedSource { get; set; }
        // The three simultaneous-access shared textures the platform copies into round-robin (null until the source's
        // extent is known and the first attach runs), and the source they are attached to (identity guards re-attach).
        public IReadOnlyList<IGpuExportableStorageImage>? GpuTargets { get; set; }
        // The human label a fault reads under: a window title, or a whole-monitor index.
        public string Label => ((MonitorIndex is { } monitor)
            ? $"monitor {monitor}"
            : $"window '{Title}'"
        );
        public Vector3 Light { get; set; }
        public bool Live { get; set; }

        public int? MonitorIndex { get; } = monitorIndex;
        public INativeImageCaptureFeed? Source { get; private set; } = source;
        public CpuSurfaceSource Surface { get; } = surface;
        public string Title { get; } = title;
        // Whether this feed rides the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and the screen
        // samples the LatestGpuSlot image), rather than the CPU-pixel Surface. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;

        private PullCadence Cadence { get; } = new(rateHz: profile.RefreshRateHz);

        public void Dispose() {
            ReleaseGpuTargets();
            Source?.Dispose();
            Source = null;
            Surface.Dispose();
        }
        public nint Handle() {
            if (GpuRoute) {
                // The sampled handle is the image-view of the platform's latest completed GPU copy; 0 (no-signal) until
                // that first copy lands. Returning a different slot handle per copy is cheap — the engine rebinds a
                // bound screen source's descriptor every frame anyway (SdfWorldEngine.SetScreenSource).
                return ((Live && (Source is { } source) && (source.LatestGpuSlot is var slot and >= 0) && (GpuTargets is { } targets) && (slot < targets.Count))
                    ? targets[slot].ImageViewHandle
                    : 0
                );
            }

            return (Live
                ? Surface.CurrentHandle
                : 0
            );
        }
        public void NotifyDeviceLost() {
            Surface.NotifyDeviceLost();
            ReleaseGpuTargets();
            Cadence.Rearm();
        }
        // Disposes the shared textures (device-owned) and forgets the attachment so the next pull reallocates them on the
        // live device. Called on a lost source, on device loss, and on disposal.
        public void ReleaseGpuTargets() {
            GpuAttachedSource = null;

            if (GpuTargets is not { } targets) {
                return;
            }

            GpuTargets = null;

            foreach (var image in targets) {
                image.Dispose();
            }
        }
        public bool ShouldPull() => Cadence.ShouldPull();
        public bool TryEnsureSource(long? adapterLuid) {
            if ((Source is { IsEnded: false })) {
                return true;
            }

            // The old target is gone: drop its final frame and clear stale state until the replacement's first frame.
            // The stale GPU attachment is left for EnsureGpuTargets to reallocate against the replacement source.
            Source?.Dispose();
            Source = null;
            Live = false;
            Fault = null;

            INativeImageCaptureFeed? next;
            var reacquired = ((MonitorIndex is { } monitor)
                ? service.TryCreateMonitorCapture(
                    monitorIndex: monitor,
                    width: profile.Width,
                    height: profile.Height,
                    refreshRateHz: profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
                : service.TryCreateWindowCapture(
                    windowTitleFragment: Title,
                    width: profile.Width,
                    height: profile.Height,
                    refreshRateHz: profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
            );

            if (!reacquired) {
                return false;
            }

            Source = next;

            return true;
        }
    }
}
