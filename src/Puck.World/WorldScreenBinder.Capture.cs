using System.Diagnostics;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.SdfVm.Views;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // The render adapter LUID a capture feed opens its platform capture on when the D3D12 GPU transport is active, or
    // null on the Vulkan/CPU path (and until the render device is first seen at publish; declared GPU-route captures
    // defer their open to the first pull, where this has resolved).
    private long? AdapterLuidForOpen() => (m_hostsOnDirectX
        ? m_renderAdapterLuid
        : null
    );
    // The one open ladder behind a capture producer source, shared by a screen row (CaptureProducer) and the
    // frame-source registry (DeclareFrameSource, WorldScreenBinder.FrameSources.cs — a HUD/overlay capture with
    // no screen row at all): the D3D12 GPU transport always defers to the pending path (the render adapter LUID is
    // not resolvable yet); otherwise an immediate CPU/GPU open is tried, then a pending feed on a platform that
    // supports window capture at all, then nothing (fault only, no feed) on a platform with none. A caller that
    // gets no feed back never retries on its own — a null return means the platform itself cannot ever open this
    // source, not a transient miss.
    private CaptureFeed? TryCreateCaptureFeed(WorldCaptureSettings capture, out string? fault) {
        var windowTitle = (capture.WindowTitle ?? string.Empty);

        if (capture.MonitorIndex is { } monitorIndex) {
            if (
                m_hostsOnDirectX &&
                m_windowCapture.IsSupported &&
                (monitorIndex >= 0)
            ) {
                fault = null;

                return NewCaptureFeed(
                    title: "",
                    profile: capture.Profile,
                    source: null,
                    monitorIndex: monitorIndex
                );
            }

            if (TryOpenMonitorCapture(
                monitorIndex: monitorIndex,
                profile: capture.Profile,
                feed: out var monitorFeed,
                fault: out var monitorFault
            )) {
                fault = null;

                return monitorFeed;
            }

            if (
                m_windowCapture.IsSupported &&
                (monitorIndex >= 0)
            ) {
                fault = monitorFault;

                return NewCaptureFeed(
                    title: "",
                    profile: capture.Profile,
                    source: null,
                    monitorIndex: monitorIndex,
                    fault: monitorFault
                );
            }

            fault = monitorFault;

            return null;
        }

        if (
            m_hostsOnDirectX &&
            m_windowCapture.IsSupported &&
            !string.IsNullOrWhiteSpace(value: windowTitle)
        ) {
            fault = null;

            return NewCaptureFeed(
                title: windowTitle,
                profile: capture.Profile,
                source: null
            );
        }

        if (TryOpenCapture(
            title: windowTitle,
            profile: capture.Profile,
            feed: out var captureFeed,
            fault: out var captureFault
        )) {
            fault = null;

            return captureFeed;
        }

        if (
            m_windowCapture.IsSupported &&
            !string.IsNullOrWhiteSpace(value: windowTitle)
        ) {
            fault = captureFault;

            return NewCaptureFeed(
                title: windowTitle,
                profile: capture.Profile,
                source: null,
                fault: captureFault
            );
        }

        fault = captureFault;

        return null;
    }
    // Samples only already-completed compositor frames. A miss holds the last frame. An ended compositor session is
    // disposed before the binder resolves a replacement target (a returning window with the same title, or a reconnected
    // monitor); reacquisition is World policy rather than a compatibility path in the platform feed. On the D3D12 GPU
    // transport the platform copies GPU-side into shared textures the screen samples directly — the CPU surface is never
    // published, only its divided-cadence readback frames feed the room glow.
    private void CaptureWindow(CaptureFeed feed, IGpuDeviceContext deviceContext) {
        if (!feed.TryEnsureSource(adapterLuid: AdapterLuidForOpen())) {
            feed.Live = false;
            feed.Fault = $"{feed.Label} is unavailable";
            // No source to sample: drop the shared images so the next open reallocates and re-attaches from scratch.
            feed.ReleaseGpuTargets();

            return;
        }

        if (
            feed.GpuRoute &&
            m_exportsSurfaces &&
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
                feed.Light = WorldImageLight.Average(bgra: glowSurface.Pixels.Span);
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
                surface: in surface
            );
            feed.Live = true;
            feed.Fault = null;
            feed.Light = WorldImageLight.Average(bgra: surface.Pixels.Span);
        } else if (!feed.Live) {
            feed.Fault = $"{feed.Label} awaiting a compositor frame";
        }
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

        var export = new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)deviceContext));
        var images = new IGpuExportableImage[3];
        var handles = new nint[images.Length];

        for (var i = 0; (i < images.Length); ++i) {
            images[i] = export.CreateSimultaneousAccessImage(
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: ((uint)height),
                width: ((uint)width)
            );
            handles[i] = images[i].SharedHandle;
        }

        // The fence the platform signals after each copy; the frame that samples a slot waits for its value on the GPU.
        var fence = export.CreateExportableFence();
        var superseded = feed.GpuTargets;
        var supersededFence = feed.GpuFence;

        // Attach first (the platform contract: attach swaps the targets in safely), then release the old allocation.
        source.AttachGpuTargets(targets: new NativeImageGpuCaptureTargets(
            SharedTargetHandles: handles,
            Width: width,
            Height: height,
            SharedFenceHandle: fence.SharedHandle
        ));
        feed.GpuTargets = images;
        feed.GpuFence = fence;
        feed.GpuSubmitter = deviceContext.Services.QueueSubmitter;
        feed.GpuAttachedSource = source;

        if (superseded is not null) {
            foreach (var image in superseded) {
                image.Dispose();
            }
        }

        supersededFence?.Dispose();
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
        slot.LiveFeed = new CaptureSlotFeed(
            binder: this,
            feed: feed
        );
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
        slot.LiveFeed = new CaptureSlotFeed(
            binder: this,
            feed: feed
        );
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} capturing monitor {monitorIndex}");
    }

    // One feed's PRESENTATION CLOCK, stated once so a webcam and a window capture cannot drift into different refresh
    // policies. Camera pixels are nondeterministic presentation input and must not freeze when authoritative simulation
    // time is paused or absent. The first pull after arming always runs; later pulls wait out the profile's whole period.
    private sealed class PullCadence(uint rateHz) {
        private readonly long m_cadenceTicks = Math.Max(
            val1: 1L,
            val2: (Stopwatch.Frequency / Math.Max(
                val1: rateHz,
                val2: 1u
            ))
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
        public IReadOnlyList<IGpuExportableImage>? GpuTargets { get; set; }
        // The shared fence the platform signals after each copy into GpuTargets, and the render device's submitter the
        // frame that samples a slot adds its wait to.
        public IGpuExportableFence? GpuFence { get; set; }
        public IGpuQueueSubmitter? GpuSubmitter { get; set; }
        // The human label a fault reads under: a window title, or a whole-monitor index.
        public string Label => ((MonitorIndex is { } monitor)
            ? $"monitor {monitor}"
            : $"window '{Title}'"
        );
        public Vector3 Light { get; set; }
        public bool Live { get; set; }

        public int? MonitorIndex { get; } = monitorIndex;
        public WorldFeedProfile Profile { get; } = profile;
        public INativeImageCaptureFeed? Source { get; private set; } = source;
        public CpuSurfaceSource Surface { get; } = surface;
        public string Title { get; } = title;
        // Whether this feed rides the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and the screen
        // samples the LatestGpuSlot image), rather than the CPU-pixel Surface. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;

        private PullCadence Cadence { get; } = new(rateHz: profile.RefreshRateHz);

        // Acquires the image a frame samples: on the GPU route, the latest published copy's slot, whose shared-fence
        // value the sampling submission waits for; otherwise the CPU surface.
        public GpuImageLease AcquireFrame() {
            if (
                GpuRoute &&
                Live &&
                (Source is { } source) &&
                (source.LatestGpuSlot is var slot and >= 0) &&
                (GpuTargets is { } targets) &&
                (slot < targets.Count)
            ) {
                var fenceValue = source.GpuSlotFenceValue(slot: slot);

                if (
                    (0UL != fenceValue) &&
                    (GpuFence is { } fence)
                ) {
                    GpuSubmitter!.AddExternalWait(wait: new GpuExternalWait(
                        Fence: fence,
                        Value: fenceValue
                    ));
                }

                return targets[slot].ImageViewHandle;
            }

            return Handle();
        }
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

            GpuFence?.Dispose();
            GpuFence = null;
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
                    width: Profile.Width,
                    height: Profile.Height,
                    refreshRateHz: Profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
                : service.TryCreateWindowCapture(
                    windowTitleFragment: Title,
                    width: Profile.Width,
                    height: Profile.Height,
                    refreshRateHz: Profile.RefreshRateHz,
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
