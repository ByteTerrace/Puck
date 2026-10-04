using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    /// <inheritdoc/>
    public void ConfigureViews(SdfWorldPipelineCatalog pipelines, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, ISdfFrameSource host, int displayWidth, int displayHeight, WorldSeatViewports viewports) {
        ArgumentNullException.ThrowIfNull(argument: pipelines);
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: viewports);

        m_viewports = viewports;
        m_viewPipelines = pipelines;
        m_viewHostsOnDirectX = hostsOnDirectX;
        m_viewProgramWordCapacity = programWordCapacity;
        m_viewInstanceCapacity = instanceCapacity;
        m_viewDynamicTransformCapacity = dynamicTransformCapacity;
        m_viewHostSource = host;
        m_viewDisplayWidth = displayWidth;
        m_viewDisplayHeight = displayHeight;

        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is { } view) &&
                (ResolveCamera(name: view.Name) is { } camera)
            ) {
                RegisterCameraView(
                    camera: camera,
                    seat: DefaultViewSeat
                );
            }
        }

        // Every session feed resolved (headless-safe, at boot or a live reconcile) but not yet registered completes its
        // view now that the render envelope is known, at every level.
        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (feed.FrameSource is null) {
                RegisterSessionView(feed: feed);
            }
        }

        ReconcileViews();
    }
    /// <summary>Changes the display extent a view's declared extent is a fraction of, as the host resizes its display:
    /// every camera and session view fits its declared extent to the new display from the next reconciliation on.</summary>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or <paramref name="displayHeight"/>
    /// is not positive.</exception>
    public void ResizeDisplay(int displayWidth, int displayHeight) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayHeight);

        m_viewDisplayWidth = displayWidth;
        m_viewDisplayHeight = displayHeight;

        ReconcileViews();
    }
    /// <summary>Publishes the screens' content for this produced frame, before the render graph schedules it: it uploads
    /// the fills a filled external source resolves to, and services the shared camera feeds, the probe outputs and the HUD's captures. A
    /// producer, machine or probe source a screen shows is a source instance the runtime publishes at its cadence when it
    /// renders the instance, and a view is an instance too, which a capture frame renders again while it is tainted. It
    /// keeps the frame's context, through which a session view captures the world's frame before its own, and ends by
    /// publishing every screen's mapping (<see cref="Mappings"/>) at the extents its images now have.</summary>
    /// <param name="context">The host's frame context, whose host resolves the live GPU device; a frame with no device publishes
    /// nothing.</param>
    public void Publish(in FrameContext context) {
        if (
            m_disposed ||
            (context.Host is not { } host) ||
            !host.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)
        ) {
            return;
        }

        ReconcileSessionLifecycles();
        RetireParkedCaptures();

        // Resolve the render adapter LUID once, backend-neutrally — the device is created lazily, so the value is
        // first available here (not at construction). Capture feeds and the camera GPU tier then open their platform
        // and shim devices on the render GPU so shared textures import across the API boundary. A driver reporting no
        // LUID (zero) stays unresolved, which the camera GPU tier reads as "sharing unavailable" and falls back on.
        if (
            (m_renderAdapterLuid is null) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        ) &&
            (deviceContext.AdapterLuid is var renderAdapterLuid) &&
            (0 != renderAdapterLuid)
        ) {
            m_renderAdapterLuid = renderAdapterLuid;
        }

        // The shared webcam owns one producer cadence and skips uploads when its asynchronous frame version has not
        // advanced. Window captures below each own an independent deadline from their declaration.
        EnsureFills(context: in context);
        CaptureCamera(
            context: in context,
            deviceContext: deviceContext
        );
        ServiceProbeFeeds(deviceContext: deviceContext);
        PublishFrameCaptures(context: in context);
        ReconcileNesting();
        SettleWindowRoutes();
        // Reconciliation may have closed a view or routed it into a shared residency this frame.
        ReconcileViewResidencies();
        m_frameContext = context;
        m_hasFrameContext = true;
        Mappings.Publish(images: this);
        PublishNestedMappings();
    }
    /// <summary>Sets the deterministic refresh divisor of every camera view and every session view but a window's. One
    /// renders every produced frame; larger values keep the last image between refreshes.</summary>
    /// <param name="divisor">Produced frames per refresh, from 1 through 8.</param>
    public void SetViewRefreshDivisor(int divisor) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: divisor,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: divisor,
            other: 8
        );

        m_viewRefreshDivisor = divisor;
        ReconcileViews();
    }
}
