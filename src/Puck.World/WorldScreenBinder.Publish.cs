using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    private readonly record struct ScreenPublishTiming(long CameraTicks, long MachineTicks, long WindowCaptureTicks, long PatternTicks) {
        public long TotalTicks => (((CameraTicks + MachineTicks) + WindowCaptureTicks) + PatternTicks);
    }

    // Reports the slowest complete screen-publication frame in each armed block. The source categories sum every slot
    // of that kind, so a tail frame immediately identifies whether live camera upload, desktop capture, emulation, or
    // procedural CPU pixels occupied the render thread without adding per-frame console IO.
    private void ReportPublishTiming(ScreenPublishTiming sample) {
        if (sample.TotalTicks >= m_publishTimingWorst.TotalTicks) {
            m_publishTimingWorst = sample;
        }

        if (0UL != (m_publishTimingFrame % PublishTimingReportInterval)) {
            return;
        }

        static double Milliseconds(long ticks) =>
            ((((double)ticks) * 1000.0) / Stopwatch.Frequency);

        var worst = m_publishTimingWorst;

        m_publishTimingWorst = default;

        Console.Error.WriteLine(value: $"[frame-timing] screen-publish worst-of-{PublishTimingReportInterval} total {Milliseconds(ticks: worst.TotalTicks):0.000}ms | camera {Milliseconds(ticks: worst.CameraTicks):0.000} | machine {Milliseconds(ticks: worst.MachineTicks):0.000} | window-capture {Milliseconds(ticks: worst.WindowCaptureTicks):0.000} | pattern {Milliseconds(ticks: worst.PatternTicks):0.000}");
    }

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
                RegisterCameraView(camera: camera);
                view.Stack = m_viewStack;
                _ = (wiredByName.TryGetValue(
                    key: camera.Name,
                    value: out var indices
                )
                    ? indices
                    : (wiredByName[camera.Name] = new HashSet<int>())).Add(item: slot.Index);
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
    /// <summary>Publishes every CPU-fed screen for this produced frame. Deterministic machines have already advanced
    /// server-side, inside <c>WorldServer.Step</c> (<c>Server.WorldMachineHost.Advance</c>); this seam only uploads
    /// their latest framebuffer (the one GPU call this project makes on a machine's behalf) and services
    /// presentation-only camera/window captures on source-owned cadences.</summary>
    /// <param name="tick">The world's completed-step ordinal driving deterministic pattern animation.</param>
    /// <param name="elapsedTicks">The exact completed simulation time in engine ticks, used by feed deadlines.</param>
    /// <param name="deviceContext">The live GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void Publish(ulong tick, ulong elapsedTicks, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (m_disposed) {
            return;
        }

        ReconcileSessionLifecycles();

        // Resolve the render adapter LUID once — the device is created lazily, so the value is first available here (not
        // at construction). Capture feeds then open their platform capture on the render GPU so the shared textures import.
        if (
            m_hostsOnDirectX &&
            (m_renderAdapterLuid is null) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        ) &&
            (deviceContext is IDirectXDeviceContext renderDeviceContext)
        ) {
            m_renderAdapterLuid = renderDeviceContext.AdapterLuid;
        }

        var timingEnabled = GpuTimingControl.Shared.Armed;
        var phaseStart = (timingEnabled
            ? Stopwatch.GetTimestamp()
            : 0L
        );

        // The shared webcam owns one producer cadence and skips uploads when its asynchronous frame version has not
        // advanced. Window captures below each own an independent deadline from their declaration.
        CaptureCamera(
            deviceContext: deviceContext,
            elapsedTicks: elapsedTicks,
            gpu: gpu
        );
        var cameraTicks = (timingEnabled
            ? (Stopwatch.GetTimestamp() - phaseStart)
            : 0L
        );
        var machineTicks = 0L;
        var windowCaptureTicks = 0L;
        var patternTicks = 0L;

        foreach (var slot in m_slots.Values) {
            if (m_machines.MachineAt(index: slot.Index) is { } machine) {
                phaseStart = (timingEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0L
                );
                machine.PublishFrame(
                    deviceContext: deviceContext,
                    gpu: gpu
                );
                machineTicks += (timingEnabled
                    ? (Stopwatch.GetTimestamp() - phaseStart)
                    : 0L
                );

                continue;
            }

            // The shared webcam is published once (in CaptureCamera above), so a camera screen only rides that feed.
            if (slot.Camera is not null) {
                continue;
            }

            if (slot.Capture is { } capture) {
                if (capture.ShouldPull(elapsedTicks: elapsedTicks)) {
                    phaseStart = (timingEnabled
                        ? Stopwatch.GetTimestamp()
                        : 0L
                    );
                    CaptureWindow(
                        deviceContext: deviceContext,
                        feed: capture,
                        gpu: gpu
                    );
                    windowCaptureTicks += (timingEnabled
                        ? (Stopwatch.GetTimestamp() - phaseStart)
                        : 0L
                    );
                }

                continue;
            }

            if (slot.Pattern is { } pattern) {
                phaseStart = (timingEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0L
                );
                var pixels = pattern.Pattern.Render(tick: tick);

                _ = pattern.Surface.Publish(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    pixels: pixels,
                    width: ((uint)pattern.Pattern.Width),
                    height: ((uint)pattern.Pattern.Height),
                    format: TestPatternSource.PixelFormat
                );

                pattern.Light = AverageColor(pixels: pixels.Span);
                patternTicks += (timingEnabled
                    ? (Stopwatch.GetTimestamp() - phaseStart)
                    : 0L
                );

                continue;
            }

            // A QR matrix is a pure function of its payload/level/quiet zone — never the tick — so it uploads exactly
            // ONCE (the first produced frame after boot, a live screen.source <index> qr, or a device loss) instead of re-copying an
            // unchanged buffer to the GPU every frame.
            if (slot.Qr is { Published: false } qrFeed) {
                phaseStart = (timingEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0L
                );

                _ = qrFeed.Surface.Publish(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    pixels: qrFeed.Pixels,
                    width: qrFeed.Width,
                    height: qrFeed.Height,
                    format: TestPatternSource.PixelFormat
                );

                qrFeed.Published = true;
                patternTicks += (timingEnabled
                    ? (Stopwatch.GetTimestamp() - phaseStart)
                    : 0L
                );
            }
        }

        if (timingEnabled) {
            ++m_publishTimingFrame;
            ReportPublishTiming(sample: new ScreenPublishTiming(
                CameraTicks: cameraTicks,
                MachineTicks: machineTicks,
                PatternTicks: patternTicks,
                WindowCaptureTicks: windowCaptureTicks
            ));
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
    public void RenderViews(in FrameContext context, SdfProgram program, int revision, IReadOnlyList<DynamicTransform> transforms, float time, ulong authoritativeTick, SdfFrame hostFrame) {
        if (
            m_disposed ||
            (m_viewStack is not { } stack)
        ) {
            return;
        }

        m_viewTransforms = transforms;

        if (m_viewRefreshCountdown > 0) {
            m_viewRefreshCountdown--;

            return;
        }

        m_viewRefreshCountdown = (m_viewRefreshDivisor - 1);

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
