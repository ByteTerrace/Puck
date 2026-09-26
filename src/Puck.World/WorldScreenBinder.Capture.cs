using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Platform;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // Captures a live verb opened to prove its target, each waiting for the source instance that shows it to adopt it.
    private readonly List<ParkedCapture> m_parkedCaptures = [];

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
    // converted, only its divided-cadence readback frames feed the room glow.
    private void CaptureWindow(CaptureFeed feed, in FrameContext context) {
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
            context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext) &&
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
            _ = TryConvert(
                context: in context,
                pixels: feed.Pixels,
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
    // (GpuTargetsOutdated) or a reacquired source; AttachGpuTargets replaces first, then the superseded ring retires,
    // disposed with its fence once no submitted frame samples it. Cadence-gated by the caller, so it never runs per
    // render frame.
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
        var images = new IGpuExportableImage[SharedTargetCount];
        var handles = new nint[images.Length];

        for (var i = 0; (i < images.Length); ++i) {
            images[i] = export.CreateSimultaneousAccessImage(
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: ((uint)height),
                width: ((uint)width)
            );
            handles[i] = images[i].SharedHandle;
        }

        // The fence the platform signals after each copy, whose value the frame that samples a slot waits for on the GPU,
        // and the publication the platform reserves and publishes slots through and the frames acquire them from.
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: images.Length);

        var ring = new SharedTargetRing(
            fence: SharedRingFence.Create(
                export: export,
                hostsOnDirectX: true,
                renderDevice: deviceContext
            ),
            images: images,
            importedViews: null,
            imports: null,
            ring: slots,
            targetDevice: null
        );
        var superseded = feed.GpuTargets;

        // Attach first (the platform contract: attach swaps the targets in safely), then retire the old ring: its images
        // and fence go once the last submitted frame sampling them has retired its lease.
        source.AttachGpuTargets(targets: new NativeImageGpuCaptureTargets(
            SharedTargetHandles: handles,
            Width: width,
            Height: height,
            Slots: slots,
            SharedFenceHandle: ring.ProducerFenceHandle
        ));
        feed.GpuTargets = ring;
        feed.GpuAttachedSource = source;
        superseded?.Retire();
    }
    // Constructs a capture feed carrying this binder's transport choice (GPU on the D3D12 host, CPU on Vulkan). The one
    // place window/monitor CaptureFeeds are built, so the route flag can never diverge across the open/pending sites.
    private CaptureFeed NewCaptureFeed(string title, WorldFeedProfile profile, INativeImageCaptureFeed? source, int? monitorIndex = null, string? fault = null) =>
        new(
            title: title,
            service: m_windowCapture,
            profile: profile,
            source: source,
            pixels: new ConvertedPixels(
                content: ImageContentClass.External,
                name: $"capture:{((monitorIndex is { } monitor) ? $"monitor {monitor}" : title)}",
                producer: WorldImageProducerSettings.CaptureId
            ),
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
    /// <c>screen.source &lt;index&gt; capture</c> path. The screen shows the capture's source instance over its row from
    /// the render graph's next frame, which adopts the capture opened here. The capture rebinds each grab, so
    /// the target window need not be open yet (it reads no signal until it appears, and rebinds if it disappears and
    /// returns); only an unopenable capture service fails here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="windowTitle">The captured window's title fragment (case-insensitive substring match).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCapture(int index, string windowTitle) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (!m_slots.ContainsKey(key: index)) {
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

        BindCapture(
            feed: feed,
            index: index,
            settings: new WorldCaptureSettings(
                Profile: WorldFeedProfile.Default,
                WindowTitle: windowTitle
            )
        );

        return (Ok: true, Message: $"screen {index} capturing '{windowTitle}'");
    }
    /// <summary>Binds a declared screen to a live whole-monitor capture keyed by index — the runtime <c>screen.source &lt;index&gt; desktop</c>
    /// path. The screen shows the capture's source instance over its row from the render graph's next frame, which adopts
    /// the capture opened here. The capture rebinds each grab, so it reads no signal until the monitor is present and
    /// reacquires if it disconnects and returns; an out-of-range index or an unopenable capture service fails
    /// here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="monitorIndex">The 0-based monitor to capture whole (0 = primary).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryDesktop(int index, int monitorIndex) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (!m_slots.ContainsKey(key: index)) {
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

        BindCapture(
            feed: feed,
            index: index,
            settings: new WorldCaptureSettings(
                MonitorIndex: monitorIndex,
                Profile: WorldFeedProfile.Default
            )
        );

        return (Ok: true, Message: $"screen {index} capturing monitor {monitorIndex}");
    }

    // Shows a live capture's source over a screen's row, parking the capture the verb opened for the source instance to
    // adopt when the render graph opens it.
    private void BindCapture(int index, CaptureFeed feed, WorldCaptureSettings settings) {
        var source = WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.CaptureId,
            settings: settings
        );

        m_parkedCaptures.Add(item: new ParkedCapture(
            feed: feed,
            source: source
        ));
        ShowLive(
            index: index,
            source: source
        );
    }
    // Hands a capture source instance the capture a live verb parked for equal settings, if one is waiting.
    private bool TryClaimParkedCapture(WorldScreenSource.Producer source, [NotNullWhen(returnValue: true)] out CaptureFeed? capture) {
        for (var index = 0; (index < m_parkedCaptures.Count); index++) {
            var parked = m_parkedCaptures[index];

            if (ImageSourceSettings.Equal(
                left: parked.Source.Settings,
                right: source.Settings
            )) {
                m_parkedCaptures.RemoveAt(index: index);
                capture = parked.Feed;

                return true;
            }
        }

        capture = null;

        return false;
    }
    // Disposes every parked capture an earlier publish already saw: the render graph opens the instances a publish's frame
    // shows right after that publish, so a capture still parked then was claimed by no instance (one already running under
    // equal settings, or no render graph at all).
    private void RetireParkedCaptures() {
        for (var index = (m_parkedCaptures.Count - 1); (index >= 0); index--) {
            var parked = m_parkedCaptures[index];

            if (parked.Published) {
                m_parkedCaptures.RemoveAt(index: index);
                parked.Feed.Dispose();
            } else {
                parked.Published = true;
            }
        }
    }
    private void DisposeParkedCaptures() {
        foreach (var parked in m_parkedCaptures) {
            parked.Feed.Dispose();
        }

        m_parkedCaptures.Clear();
    }

    // A capture a live verb opened, waiting for the source instance that shows it.
    private sealed class ParkedCapture(WorldScreenSource.Producer source, CaptureFeed feed) {
        public CaptureFeed Feed { get; } = feed;

        // Whether a publish has run since the capture was parked.
        public bool Published { get; set; }

        public WorldScreenSource.Producer Source { get; } = source;
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
        ConvertedPixels pixels,
        bool gpuRoute = false,
        int? monitorIndex = null
    ) : IDisposable {
        public string? Fault { get; set; }
        public INativeImageCaptureFeed? GpuAttachedSource { get; set; }
        // The ring of simultaneous-access shared textures, with its shared fence and slot publication, the platform copies
        // into (null until the source's extent is known and the first attach runs); GpuAttachedSource is the source it is
        // attached to (identity guards re-attach).
        public SharedTargetRing? GpuTargets { get; set; }
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
        // The CPU route's pixels, converted into the image a frame samples.
        public ConvertedPixels Pixels { get; } = pixels;
        public string Title { get; } = title;
        // Whether this feed rides the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and a frame acquires
        // their latest slot), rather than the converted CPU Pixels. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;

        private PullCadence Cadence { get; } = new(rateHz: profile.RefreshRateHz);

        // Acquires the image a frame samples: on the GPU route, the latest published copy's slot, held against the
        // platform's next writes until the lease retires and carrying the shared-fence value its submission waits for;
        // otherwise the converted CPU pixels, held until the frame retires.
        public GpuImageLease AcquireFrame() {
            if (!Live) {
                return 0;
            }

            if (GpuRoute) {
                return (((GpuTargets is { } ring) && ring.TryAcquire(frame: out var frame))
                    ? frame
                    : 0);
            }

            return Pixels.Acquire();
        }
        public void Dispose() {
            ReleaseGpuTargets();
            Source?.Dispose();
            Source = null;
            Pixels.Retire();
        }
        public nint Handle() {
            if (GpuRoute) {
                // The image view of the platform's latest completed GPU copy; 0 (no-signal) until that first copy lands.
                return ((Live && (GpuTargets is { } ring))
                    ? ring.LatestHandle()
                    : 0
                );
            }

            return (Live
                ? Pixels.Handle
                : 0
            );
        }
        public void NotifyDeviceLost() {
            Pixels.OnDeviceLost();
            ReleaseGpuTargets();
            Cadence.Rearm();
        }
        // Retires the shared ring (device-owned; disposed once no submitted frame samples it) and forgets the attachment so
        // the next pull reallocates it on the live device. Called on a lost source, on device loss, and on disposal.
        public void ReleaseGpuTargets() {
            var ring = GpuTargets;

            GpuAttachedSource = null;
            GpuTargets = null;
            ring?.Retire();
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
