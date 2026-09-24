using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    /// <summary>Stands up the offscreen view pool backing every declared View (jumbotron) screen — called once by the
    /// render factory after the frame source has probed the render envelope (the worst-case program/instance/transform
    /// capacities every offscreen view render must fit). Registers one persistent <see cref="SdfCameraView"/> per
    /// referenced camera, posed by either its declared <see cref="FixedRig"/> or an avatar-anchored
    /// <see cref="FirstPersonRig"/>, and records each view's
    /// self-reference screen set (a screen wired to view V binds 0 inside V's own render — no feedback compounding).
    /// A no-op when the world declares no View screen (no pool is created, so a plain world pays nothing).</summary>
    /// <param name="services">The concrete GPU-services closure (<see cref="SdfViewGpuServices"/>) every offscreen
    /// camera view this binder later constructs forwards to its engine — resolved once, eagerly, at the composition
    /// root and stashed here unchanged (never a retained <see cref="IServiceProvider"/> to re-resolve from later;
    /// see <see cref="RegisterCameraView"/>, this binder's one construction site).</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the offscreen kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public void ConfigureViews(SdfViewGpuServices services, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity) {
        ArgumentNullException.ThrowIfNull(argument: services);

        m_viewServices = services;
        m_viewHostsOnDirectX = hostsOnDirectX;
        m_viewProgramWordCapacity = programWordCapacity;
        m_viewInstanceCapacity = instanceCapacity;
        m_viewDynamicTransformCapacity = dynamicTransformCapacity;

        // The screen indices wired to each referenced camera name (a name shared by two jumbotrons self-references both).
        var wiredByName = new Dictionary<string, HashSet<int>>(comparer: StringComparer.Ordinal);

        foreach (var slot in m_slots.Values) {
            if (
                (slot.View is { } view) &&
                (ResolveCamera(name: view.Name) is { } camera)
            ) {
                RegisterCameraView(
                    camera: camera,
                    seat: DefaultViewSeat
                );
                view.Stack = m_viewStack;
                _ = (wiredByName.TryGetValue(
                    key: view.Name,
                    value: out var indices
                )
                    ? indices
                    : (wiredByName[view.Name] = new HashSet<int>())).Add(item: slot.Index);
            }
        }

        if (m_viewStack is { } stack) {
            foreach (var (name, indices) in wiredByName) {
                stack.SetWiredScreens(
                    name: name,
                    screenIndices: indices
                );
            }
        }

        // Every session-sourced slot resolved (headless-safe, at boot or a live reconcile) but not yet GPU-registered
        // — completes the offscreen WorldSessionView registration now that the render envelope is known, exactly as
        // a declared View camera's SdfCameraView completes here rather than at construction.
        foreach (var slot in m_slots.Values) {
            if (
                (slot.Session is { } feed) &&
                (feed.View is null)
            ) {
                RegisterSessionView(
                    index: slot.Index,
                    feed: feed
                );
            }
        }
    }
    /// <summary>Publishes every screen's producer feed for this produced frame. Deterministic machines have already
    /// advanced server-side, inside <c>WorldServer.Step</c> (<c>Server.WorldMachineHost.Advance</c>); this seam only
    /// uploads their latest framebuffer (the one GPU call this project makes on a machine's behalf) and services each
    /// producer feed on its own cadence. It advances the capture gate first, so every source this frame resolves sees
    /// the same answer, and uploads the fills a filled external source resolves to.</summary>
    /// <param name="tick">The world's completed-step ordinal driving deterministic pattern animation.</param>
    /// <param name="deviceContext">The live GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void Publish(ulong tick, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (m_disposed) {
            return;
        }

        m_captureGate.BeginFrame();
        ReconcileSessionLifecycles();

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
        EnsureFills(
            deviceContext: deviceContext,
            gpu: gpu
        );
        CaptureCamera(
            deviceContext: deviceContext,
            gpu: gpu
        );
        ServiceProbeFeeds(deviceContext: deviceContext);
        PublishFrameCaptures(
            deviceContext: deviceContext,
            gpu: gpu
        );

        m_publishedMachineOutputs.Clear();

        foreach (var slot in m_slots.Values) {
            if (slot.MachineSource is { } source) {
                if (
                    (m_machines.VideoOutput(
                    instance: source.Instance,
                    output: source.Output
                ) is { } machine) &&
                    m_publishedMachineOutputs.Add(item: (source.Instance, source.Output))
                ) {
                    machine.PublishFrame(
                        deviceContext: deviceContext,
                        gpu: gpu
                    );
                }

                // A named output is one producer shared by every display that references it. Once the first
                // consumer has published the current frame, the remaining consumers only resolve that same handle.
                continue;
            }

            // A live feed hides the declared one, so only the shown feed publishes; a probe output was published once
            // above (ServiceProbeFeeds), and the shared webcam's feed publishes nothing of its own.
            if (slot.LiveFeed is { } live) {
                live.Publish(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    tick: tick
                );

                continue;
            }

            if (slot.Probe is not null) {
                continue;
            }

            slot.DeclaredFeed?.Publish(
                deviceContext: deviceContext,
                gpu: gpu,
                tick: tick
            );
        }
    }
    /// <summary>Renders this frame's jumbotron views against the live device — called from the frame source's
    /// <see cref="ISdfFrameSource.RenderViews"/> seam after the CPU-fed screens have published and before the engine polls
    /// the source providers, so a View screen's provider returns a handle to this frame's offscreen render. Each view's
    /// own render sees every other screen surface as the room shows it (a jumbotron films the lit test pattern / booted
    /// machine beside it) and its own face as unbound (the self-reference rule). A no-op with no view pool.</summary>
    /// <param name="context">This frame's host frame context (resolves the offscreen device).</param>
    /// <param name="program">This frame's composed world program (the same instance the main engine renders).</param>
    /// <param name="revision">The program's revision counter — each offscreen engine re-uploads only when it advances.</param>
    /// <param name="transforms">This frame's packed dynamic transforms, identical to the main engine's.</param>
    /// <param name="time">The frame's content clock (seconds) — the views render the same animated world the room does.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    /// <param name="hostFrame">The frame the room is rendering this frame. Offscreen content derives its own
    /// submission from this rather than building one beside it, so every per-frame lever reaches a jumbotron by
    /// construction (see <c>SdfCameraView.Resolve</c>).</param>
    public void RenderViews(in FrameContext context, SdfProgram program, int revision, DynamicTransform[] transforms, float time, ulong authoritativeTick, SdfFrame hostFrame) {
        if (
            m_disposed ||
            (m_viewStack is not { } stack)
        ) {
            return;
        }

        m_viewTransforms = transforms;

        // The first frame the capture gate fills renders the views again when an external source is bound, so no view
        // shows an image it rendered from that source before the gate began filling. A host that fills every frame
        // never rendered one unfilled, so its cadence is untouched.
        if (
            m_captureGate.Filling &&
            !m_viewsRenderedFilling &&
            BindsExternal()
        ) {
            m_viewRefreshCountdown = 0;
        }

        if (m_viewRefreshCountdown > 0) {
            m_viewRefreshCountdown--;

            return;
        }

        m_viewRefreshCountdown = (m_viewRefreshDivisor - 1);
        m_viewsRenderedFilling = m_captureGate.Filling;

        UpdateWindowCameras();

        stack.RenderFrame(context: new ViewRenderContext(
            Host: context,
            HostFrame: hostFrame,
            Program: program,
            ProgramRevision: revision,
            Time: time,
            AuthoritativeTick: authoritativeTick,
            // What each screen surface binds INSIDE a jumbotron's render: the same handle the room shows (the ViewStack
            // zeroes the view's own wired screens per the self-reference rule, so this need not).
            ResolveScreenSource: CurrentHandle
        ));
    }
    /// <summary>Sets the deterministic jumbotron refresh divisor. One renders every produced frame; larger values keep
    /// the last resolved image between refreshes, using <see cref="ViewStack"/>'s existing persistent-handle contract.</summary>
    /// <param name="divisor">Produced frames per offscreen refresh, from 1 through 8.</param>
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
        m_viewRefreshCountdown = 0;
    }
}
