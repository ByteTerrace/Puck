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
using Puck.Hosting;
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
    // Services the ONE shared webcam feed. On BOTH hosts it rides the GPU-resident zero-copy tier — the platform's
    // decode device converts frames on-GPU and copies them into the three shared textures provisioned here, so the
    // screen samples the latest published slot directly and no frame ever visits host memory — falling back to the
    // CPU-pixel tier exactly once if the GPU open refuses (no adapter LUID, no device, a failed target or import).
    // The CPU tier pulls one frame on the capture cadence and publishes it to the shared surface, refreshing the
    // handle + room glow. A disconnected device drops the feed to unbound + fault on either tier.
    private void CaptureCamera(ulong elapsedTicks, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (m_cameraFeed is not { } feed) {
            return;
        }

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

        var version = session.FrameVersion;

        if (
            (version == feed.LastFrameVersion) ||
            !feed.ShouldPull(elapsedTicks: elapsedTicks)
        ) {
            return;
        }

        if (session.TryCapture(surface: out var surface)) {
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
        } else {
            // The async producer advertised a new version but the grab raced it. Do not spend the declaration's whole
            // cadence on that miss; retry on the next produced frame while still avoiding more than one attempt here.
            feed.RetryPull();
        }
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
                session: out var opened
            )) {
                FallBackToCpuCamera(feed: feed);

                return;
            }

            var images = new IGpuExportableStorageImage[3];
            var handles = new nint[images.Length];
            IGpuSurfaceImport[]? imports = null;
            nint[]? importedViews = null;

            try {
                var targetContext = (m_hostsOnDirectX
                    ? deviceContext
                    : (m_cameraTargetDevice ??= new DirectXDeviceContext(
                        adapterLuid: adapterLuid,
                        deviceApi: new DirectXNativeDeviceApi(),
                        minimumFeatureLevel: DirectXFeatureLevel.Level110
                    ))
                );

                var export = (m_cameraExport ??= new DirectXGpuSurfaceExportFactory());

                for (var i = 0; (i < images.Length); ++i) {
                    images[i] = export.CreateSimultaneousAccessStorageImage(
                        deviceContext: targetContext,
                        format: GpuPixelFormat.B8G8R8A8Unorm,
                        height: ((uint)opened.Height),
                        width: ((uint)opened.Width)
                    );
                    handles[i] = images[i].SharedHandle;
                }

                if (!m_hostsOnDirectX) {
                    // One importer per slot — the Vulkan import caches a single image per importer, and the three
                    // slots must all stay live for the round-robin publication to sample.
                    var transfers = (m_surfaceTransfers ?? throw new InvalidOperationException(message: "the Vulkan camera GPU tier needs the surface-transfer factory (absent on a headless boot)"));

                    imports = new IGpuSurfaceImport[images.Length];
                    importedViews = new nint[images.Length];

                    for (var i = 0; (i < images.Length); ++i) {
                        imports[i] = transfers.CreateImport(deviceContext: deviceContext);
                        importedViews[i] = imports[i].Import(
                            deviceContext: deviceContext,
                            format: GpuPixelFormat.B8G8R8A8Unorm,
                            height: ((uint)opened.Height),
                            sharedHandle: handles[i],
                            width: ((uint)opened.Width)
                        ).ImageViewHandle;
                    }
                }

                opened.Start(sharedTargetHandles: handles);
            } catch (Exception exception) {
                Console.Error.WriteLine(value: $"[camera] GPU tier start failed: {exception.Message}; falling back to the CPU tier.");

                if (imports is not null) {
                    foreach (var import in imports) {
                        import?.Dispose();
                    }
                }

                foreach (var image in images) {
                    image?.Dispose();
                }

                opened.Dispose();
                FallBackToCpuCamera(feed: feed);

                return;
            }

            Console.Out.WriteLine(value: $"[camera] GPU tier: '{opened.Name}' {opened.Width}x{opened.Height}, {images.Length} shared targets on the render adapter{(m_hostsOnDirectX ? "" : ", imported for Vulkan sampling")}.");
            feed.SharedSession = opened;
            feed.GpuTargets = images;
            feed.GpuImports = imports;
            feed.GpuImportedViews = importedViews;
            // The idempotence cache belongs to the control surface, not the feed: this is a newly opened device.
            feed.AppliedControls = null;
            session = opened;
            ApplyCameraControls(
                desired: m_cameraControls,
                feed: feed
            );
        }

        if (session.IsEnded) {
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
    ];

    // Pushes the authored control state onto the live session's device, per control: a PRESENT member sets the control
    // manual at that value (device-clamped), and a member the author REMOVED (present in the last applied state, absent
    // now) restores its driver default — a member never authored never disturbs driver state at all. Best-effort per
    // control (the device is authoritative; screen.camera reads the result back), and a no-op while the authored state
    // matches what was last applied, so the reconcile path calls this freely.
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

        feed.AppliedControls = desired;
    }

    /// <summary>Describes the shared camera feed's live control surface — the <c>screen.camera</c> read-back: the
    /// device name, tier, negotiated extent, and each device-supported control's current value/mode, device envelope,
    /// and authored document value. Null when no camera feed exists (the fault is <c>m_cameraFault</c>).</summary>
    /// <returns>The single-line description, or <see langword="null"/> when no camera feed is live.</returns>
    public string? DescribeCamera() {
        if (m_cameraFeed is not { } feed) {
            return null;
        }

        var session = ((object?)feed.SharedSession ?? feed.Session);
        var tier = ((feed.SharedSession is not null)
            ? "gpu"
            : ((feed.Session is not null)
                ? "cpu"
                : (feed.GpuRoute ? "pending" : "unopened")
            )
        );

        if (session is not ICameraControlSurface surface) {
            return $"'{m_cameraFault}' {tier}";
        }

        var (name, width, height) = ((feed.SharedSession is { } shared)
            ? (shared.Name, shared.Width, shared.Height)
            : (feed.Session!.Name, feed.Session!.Width, feed.Session!.Height)
        );
        var builder = new StringBuilder();

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"'{name}' {tier} {width}x{height}"
        );

        foreach (var (control, label, select) in CameraControlMap) {
            if (!surface.TryGetRange(control: control, range: out var range)) {
                continue;
            }

            _ = surface.TryGet(control: control, value: out var value, auto: out var auto);

            var authored = ((m_cameraControls is null) ? null : select(arg: m_cameraControls));

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" — {label} {value}({(auto ? "auto" : "manual")}) [{range.Minimum}..{range.Maximum}]{(range.SupportsAuto ? "+auto" : "")} authored {((authored is { } a) ? a.ToString(provider: CultureInfo.InvariantCulture) : "none")}"
            );
        }

        return builder.ToString();
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
            session: out var session
        )) {
            feed.Session = session;
            ApplyCameraControls(
                desired: m_cameraControls,
                feed: feed
            );
        } else {
            m_cameraFault = "no camera device present";
            feed.Fault = m_cameraFault;
            feed.Live = false;
        }
    }
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
    // Opens (once) and returns the ONE shared webcam feed, or null when no device can be opened (m_cameraFault holds
    // the reason). Every camera screen shares this single feed — two sessions on one physical device flicker. On a
    // modern Windows host — EITHER backend — the feed starts PENDING on the GPU-resident tier (the render adapter
    // LUID it must open on resolves at first publish, so CaptureCameraGpu completes the open and falls back to the
    // CPU tier if it refuses) — the bind succeeds now and a device absence surfaces as the feed's own fault.
    // Elsewhere the CPU-pixel session opens here, synchronously.
    private CameraFeed? EnsureCameraFeed(WorldFeedProfile profile) {
        if (m_cameraFeed is not null) {
            return m_cameraFeed;
        }

        if (m_cameraTried) {
            return null;
        }

        m_cameraTried = true;

        if (!m_cameraCapture.IsSupported) {
            m_cameraFault = "no camera device present";

            return null;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            m_cameraFeed = new CameraFeed(
                profile: profile,
                surface: new CpuSurfaceSource(),
                gpuRoute: true
            );

            return m_cameraFeed;
        }

        if (!m_cameraCapture.TryOpenDefault(
            requestedWidth: profile.Width,
            requestedHeight: profile.Height,
            requestedRateHz: profile.RefreshRateHz,
            session: out var session
        )) {
            m_cameraFault = "no camera device present";

            return null;
        }

        m_cameraFeed = new CameraFeed(
            profile: profile,
            surface: new CpuSurfaceSource(),
            gpuRoute: false
        ) {
            Session = session,
        };
        ApplyCameraControls(
            desired: m_cameraControls,
            feed: m_cameraFeed
        );

        return m_cameraFeed;
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
    // One physical default-camera session is shared to avoid device flicker. When several camera sources declare
    // different preferences, request the richest combined envelope rather than letting declaration order choose.
    private static WorldFeedProfile ResolveSharedCameraProfile(IReadOnlyList<WorldScreen> screens) {
        var profile = WorldFeedProfile.Default;
        var found = false;

        foreach (var screen in screens) {
            if (screen.Source is not WorldScreenSource.Camera camera) {
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

    /// <summary>Binds a declared screen to the shared live webcam feed — the runtime <c>screen.source &lt;index&gt; camera</c> path. Any
    /// existing producer on the slot is cleared first. Fails loudly for an undeclared screen or when no camera device can
    /// be opened.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCamera(int index) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (EnsureCameraFeed(profile: WorldFeedProfile.Default) is not { } feed) {
            return (Ok: false, Message: m_cameraFault);
        }

        slot.ClearLive();
        slot.Camera = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing the webcam");
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

    // One feed's PULL CLOCK, stated once so a webcam and a window capture cannot drift into different refresh
    // policies: the first pull after arming always runs, and every later one waits out the profile's whole period.
    // Rearming (a device loss, or a pull that produced nothing) makes the next pull immediate again.
    private sealed class PullCadence(ulong cadenceTicks) {
        private readonly ulong m_cadenceTicks = cadenceTicks;

        private ulong m_lastPullTicks;
        private bool m_pulled;

        public void Rearm() => m_pulled = false;
        public bool ShouldPull(ulong elapsedTicks) {
            if (
                m_pulled &&
                ((elapsedTicks - m_lastPullTicks) < m_cadenceTicks)
            ) {
                return false;
            }

            m_pulled = true;
            m_lastPullTicks = elapsedTicks;

            return true;
        }
    }
    // The ONE shared webcam feed, on one of two tiers fixed by FallBackToCpuCamera at most once: the D3D12 GPU-resident
    // tier (SharedSession + GpuTargets — the screen samples the latest published slot's image view directly, and no CPU
    // pixels ever exist, so Light stays dark) or the CPU-pixel tier (Session + Surface — frames read back to host
    // memory, fitted, uploaded, and averaged into the room glow). A mutable class so the sessions flip in place; the
    // handle is 0 (unbound) until the first frame lands and whenever the feed is not live.
    private sealed class CameraFeed(WorldFeedProfile profile, CpuSurfaceSource surface, bool gpuRoute) : IDisposable {
        // The control state last pushed onto the device (ApplyCameraControls' idempotence + removed-member baseline);
        // null until a first apply, so an unauthored world never touches driver defaults.
        public WorldCameraControls? AppliedControls { get; set; }
        public string? Fault { get; set; }
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

        private PullCadence Cadence { get; } = new(cadenceTicks: EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz));

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
        public bool ShouldPull(ulong elapsedTicks) => Cadence.ShouldPull(elapsedTicks: elapsedTicks);
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

        private PullCadence Cadence { get; } = new(cadenceTicks: EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz));

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
        public bool ShouldPull(ulong elapsedTicks) => Cadence.ShouldPull(elapsedTicks: elapsedTicks);
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
