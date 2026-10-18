using Puck.Platform;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
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
    // Constructs a capture feed carrying this binder's transport choice (GPU on the D3D12 host, CPU on Vulkan). The one
    // place window/monitor CaptureFeeds are built, so the route flag can never diverge across the open/pending sites.
    private CaptureFeed NewCaptureFeed(string title, WorldFeedProfile profile, INativeImageCaptureFeed? source, int? monitorIndex = null, string? fault = null) =>
        new(
            title: title,
            service: m_windowCapture,
            profile: profile,
            source: source,
            pixels: new WorldCapturePixels(name: $"capture:{((monitorIndex is { } monitor) ? $"monitor {monitor}" : title)}"),
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
    /// the target window need not be open yet (it shows unbound glass until it appears, and rebinds if it disappears
    /// and returns); only an unopenable capture service fails here.</summary>
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
    /// the capture opened here. The capture rebinds each grab, so it shows unbound glass until the monitor is present
    /// and reacquires if it disconnects and returns; an out-of-range index or an unopenable capture service fails
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
        Rebind(
            live: source,
            slot: m_slots[index]
        );
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
    // One window or monitor capture: the platform feed it pulls, the pixels it converts, the shared ring the platform
    // copies into on the GPU route, and its live, fault and glow state. MonitorIndex null is window mode; non-null is
    // whole-monitor mode.
    private sealed partial class CaptureFeed(
        string title,
        INativeImageCaptureService service,
        WorldFeedProfile profile,
        INativeImageCaptureFeed? source,
        WorldCapturePixels pixels,
        bool gpuRoute = false,
        int? monitorIndex = null
    ) : IDisposable {
        public string? Fault { get; set; }
        // The ring of simultaneous-access shared textures, with its shared fence and slot publication, the platform copies
        // into (null until the source's extent is known and the first attach runs); GpuAttachedSource is the source it is
        // attached to (identity guards re-attach).
        public SharedTargetRing? GpuTargets { get; set; }
        public INativeImageCaptureFeed? GpuAttachedSource { get; set; }
        public int? MonitorIndex { get; } = monitorIndex;
        public WorldFeedProfile Profile { get; } = profile;
        public INativeImageCaptureFeed? Source { get; private set; } = source;
        // The captured frames a frame samples once converted: the CPU route's pixels, or an HDR display's GPU copies.
        public WorldCapturePixels Pixels { get; } = pixels;
        public string Title { get; } = title;
        // Whether this feed may ride the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and a frame
        // acquires their latest slot), rather than the converted CPU Pixels. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;

        private INativeImageCaptureService Service { get; } = service;

        public void Dispose() {
            ReleaseGpuTargets();
            Source?.Dispose();
            Source = null;
            Pixels.Dispose();
        }
        // Retires the shared ring (device-owned; disposed once no submitted frame samples it) and forgets the attachment so
        // the next pull reallocates it on the live device. Called on a lost source, on device loss, and on disposal.
        public void ReleaseGpuTargets() {
            var ring = GpuTargets;

            GpuAttachedSource = null;
            GpuTargets = null;
            RetireRing(ring: ring);
        }
    }
}