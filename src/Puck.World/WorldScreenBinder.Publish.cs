using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    /// <summary>Configures the views the world renders beside its own — called once by the render factory after the frame
    /// source has probed the render envelope (the worst-case program, instance and transform capacities every view's
    /// engine must fit). Registers one camera view per camera a screen names and one session view per session screen, each
    /// an <c>sdf.world</c> instance the render graph runs (<see cref="TryViewProducer"/>).</summary>
    /// <param name="pipelines">The composition's pipeline cache every view's engine leases its pipeline set from.</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <param name="host">The world's frame source, whose glyph atlas, screen decals and moving screens a camera view
    /// shares.</param>
    /// <param name="displayWidth">The display's width, in pixels, which a view's declared extent is a fraction of.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> or <paramref name="host"/> is
    /// <see langword="null"/>.</exception>
    public void ConfigureViews(SdfWorldPipelineCache pipelines, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, ISdfFrameSource host, int displayWidth, int displayHeight) {
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
    /// <summary>Publishes the screens' content for this produced frame, before the render graph schedules it: it advances
    /// the capture gate first, so every source this frame resolves sees the same answer, uploads the fills a filled
    /// external source resolves to, and services the shared camera feeds, the probe outputs and the HUD's captures. A
    /// producer, machine or probe source a screen shows is a source instance the runtime publishes at its cadence when it
    /// renders the instance, and a view is an instance too. It fits every window session's camera to the local eye, and on
    /// the first frame the gate fills while a screen shows external content it composes the views again, so every view
    /// renders over the fills before a capture reads it. It ends by publishing every screen's mapping
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

        m_captureGate.BeginFrame();
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

        // A view refreshing at a divisor may hold an image it rendered from an external source before the gate began
        // filling; composing the views again restarts their scheduling, so each renders over the fills this frame. A host
        // that fills every frame never rendered one unfilled, so its cadence is untouched.
        if (
            m_captureGate.Filling &&
            !m_viewsRenderedFilling &&
            BindsExternal()
        ) {
            ReconcileViews(force: true);
        }

        m_viewsRenderedFilling = m_captureGate.Filling;

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
