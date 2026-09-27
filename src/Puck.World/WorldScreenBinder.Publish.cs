using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    /// <summary>Configures the views the world renders beside its own — called once by the render factory after the frame
    /// source has probed the render envelope (the worst-case program, instance and transform capacities every view's
    /// residency must fit). Registers one camera view per camera a screen names and one session view per session screen,
    /// each an <c>sdf.world</c> instance the render graph runs (<see cref="TryResolveView"/>).</summary>
    /// <param name="pipelines">The composition's pipeline catalog every view's residency leases its pipelines from.</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The world's probed program-word floor.</param>
    /// <param name="instanceCapacity">The world's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The world's dynamic-transform slot count.</param>
    /// <param name="host">The world's frame source, whose glyph atlas, screen decals and moving screens a camera view
    /// shares.</param>
    /// <param name="displayWidth">The display's width, in pixels, which a view's declared extent is a fraction of.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> or <paramref name="host"/> is
    /// <see langword="null"/>.</exception>
    public void ConfigureViews(SdfWorldPipelineCatalog pipelines, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, ISdfFrameSource host, int displayWidth, int displayHeight) {
        ArgumentNullException.ThrowIfNull(argument: pipelines);
        ArgumentNullException.ThrowIfNull(argument: host);

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

        // Every session-sourced slot resolved (headless-safe, at boot or a live reconcile) but not yet registered completes
        // its view now that the render envelope is known.
        foreach (var slot in m_slots.Values) {
            if (slot.Session is { FrameSource: null } feed) {
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
    /// fits every window session's camera to the local eye, and ends by publishing every screen's mapping
    /// (<see cref="Mappings"/>) at the extents its images now have.</summary>
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

        // A view whose registration or session is gone gives back its residency.
        ReconcileViewResidencies();
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
        UpdateWindowCameras();
        Mappings.Publish(images: this);
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
