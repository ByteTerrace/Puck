using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.SdfVm.Views;

/// <summary>
/// Renders an SDF world into an offscreen image. A small <see cref="SdfWorldEngine"/> is posed for each resolve by an
/// <see cref="ISdfCameraRig"/> against a
/// live pose an <see cref="ISdfAnchorSource"/> resolves by id (see <see cref="SdfAnchorTable"/>) — or, for a rig that
/// ignores its anchor entirely (<see cref="FixedRig"/>), no anchor binding at all.
/// <para>
/// LIFETIME: this type owns a real GPU resource (an offscreen engine, lazily built on first <see cref="Resolve"/>)
/// and is meant to be registered ONCE per logical camera and kept alive across frames (see
/// <see cref="ViewStack.Register"/>'s remarks) — a caller that wants to change what this camera films or how it is
/// posed mutates <see cref="Rig"/>/<see cref="AnchorSource"/>/<see cref="AnchorIdSource"/> in place rather than
/// constructing a fresh instance, which would rebuild the GPU engine for nothing.
/// </para>
/// </summary>
public sealed class SdfCameraView : IViewContent, IDisposable {
    /// <summary>The view's fixed render width — the native brick panel size, matching every other diegetic screen
    /// source in the overworld (KEEP IN SYNC with the overworld's 160:144 screen authoring).</summary>
    public const uint DefaultWidth = 160;
    /// <summary>The view's fixed render height.</summary>
    public const uint DefaultHeight = 144;

    private readonly IServiceProvider m_services;
    private readonly bool m_hostsOnDirectX;
    private readonly int m_programWordCapacity;
    private readonly int m_instanceCapacity;
    private readonly int m_dynamicTransformCapacity;
    private readonly uint m_width;
    private readonly uint m_height;
    private SdfWorldEngine? m_engine;
    private SdfWorldKernels? m_kernels;
    private SdfProgram? m_currentProgram;
    private int m_lastUploadedRevision = -1;

    /// <summary>Initializes a camera view against the host's worst-case capacity envelope, so this view's own program
    /// upload never throws when the shared program grows within that ceiling (same contract as
    /// <c>CameraFeedPool</c>'s constructor).</summary>
    /// <param name="services">The application services (resolves the neutral GPU compute factories).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <param name="width">The render width (default the native panel size).</param>
    /// <param name="height">The render height (default the native panel size).</param>
    public SdfCameraView(IServiceProvider services, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, uint width = DefaultWidth, uint height = DefaultHeight) {
        ArgumentNullException.ThrowIfNull(services);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_width = width;
        m_height = height;
    }

    /// <summary>Resolves this view's anchor id every frame (see <see cref="AnchorSource"/>) — null or a resolved id
    /// that fails to resolve leaves the rig's <c>anchor</c> parameter at <see langword="default"/> (a
    /// <see cref="FixedRig"/> ignores it regardless).</summary>
    public ISdfAnchorSource? AnchorSource { get; set; }

    /// <summary>Resolves this view's live anchor id fresh every frame (a name→id lookup, typically
    /// <c>SdfAnchorTable.TryResolveId</c>) — a delegate rather than a cached int so a name published only SOME ticks
    /// (a companion that despawned) is re-checked rather than sticking to a stale id.</summary>
    public Func<int>? AnchorIdSource { get; set; }

    /// <summary>The rig that poses this camera from its resolved anchor. Null resolves to no signal (0) — a view
    /// registered before its rig is known (should not normally happen; every constructor path assigns one before
    /// first <see cref="Resolve"/>).</summary>
    public ISdfCameraRig? Rig { get; set; }

    /// <summary>Whether this offscreen camera skips soft shadows. Defaults to false; low-resolution diegetic displays
    /// may opt in independently of the host world's lighting quality.</summary>
    public bool DisableSoftShadows { get; set; }

    /// <summary>Whether this offscreen camera skips ambient occlusion. Defaults to false; low-resolution diegetic
    /// displays may opt in independently of the host world's lighting quality.</summary>
    public bool DisableAmbientOcclusion { get; set; }

    /// <inheritdoc/>
    /// <remarks>Always zero — a camera FILMS an already-lit world; it is not itself a light source (matches
    /// <c>CameraFeedPool</c>'s camera feeds, which reported no light of their own).</remarks>
    public Vector3 RoomGlow => Vector3.Zero;

    /// <inheritdoc/>
    /// <remarks>Always <see langword="true"/> — a camera resolve is a real offscreen render pass.</remarks>
    public bool IsBudgeted => true;

    /// <inheritdoc/>
    public nint Resolve(in ViewRenderContext context) {
        if (
            (Rig is not { } rig) ||
            (m_services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu) ||
            !context.Host.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)
        ) {
            return 0;
        }

        // A view BOUND to an anchor (AnchorSource set) needs that anchor LIVE this frame — an id that fails to resolve
        // (a companion shape not yet packed, a placement that just despawned) reports no signal (0) rather than
        // rendering from a bogus default(SdfAnchor) pose, matching the anchor table's own "stops publishing, stops
        // resolving" contract. An UNBOUND view (AnchorSource null — a World-anchored eye via FixedRig-shaped math,
        // which ignores its anchor parameter entirely) always renders.
        var anchor = default(SdfAnchor);

        if (AnchorSource is { } source) {
            if ((AnchorIdSource?.Invoke() is not { } anchorId) || !source.TryResolveAnchor(anchorId: anchorId, anchor: out anchor)) {
                return 0;
            }
        }

        EnsureEngine(device: device, gpu: gpu, program: context.Program);
        Rebuild(program: context.Program, revision: context.ProgramRevision);

        var (eye, target, fovRadians) = rig.Resolve(anchor: in anchor, time: context.Time);

        for (var screenIndex = 0; (screenIndex < SdfProgramBuilder.MaxScreenSurfaces); screenIndex++) {
            m_engine!.SetScreenSource(screenIndex: screenIndex, imageViewHandle: context.ResolveScreenSource(arg: screenIndex));
        }

        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: fovRadians,
            position: eye,
            target: target,
            viewportHeight: m_height,
            viewportWidth: m_width
        );
        var frame = new SdfFrame(
            Program: m_currentProgram!,
            ProgramChanged: false,
            Time: context.Time,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            WarpAmount: 0f
        ) {
            DynamicTransforms = context.DynamicTransforms,
            DisableAmbientOcclusion = DisableAmbientOcclusion,
            DisableSoftShadows = DisableSoftShadows,
        };

        m_engine!.SubmitFrame(frame: frame);

        return m_engine.OutputImageViewHandle;
    }

    private void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program) {
        if (m_engine is not null) {
            return;
        }

        m_kernels ??= SdfWorldKernels.Load(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_hostsOnDirectX));
        m_currentProgram ??= program;

        // GPU performance counters: same live arming as SdfEngineNode.EnsureEngine — GpuTimingControl.Shared, gated on
        // the backend having registered the timing seam. A [view-timing]-tagged offscreen engine, so its own per-pass GPU ms are
        // distinguishable from the host world's [world-timing] in a mixed log.
        IGpuTimingPoolFactory? timingFactory = null;
        IGpuTimingRecorder? timingRecorder = null;

        if (ViewTiming.Enabled) {
            timingFactory = (m_services.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory);
            timingRecorder = (m_services.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder);
        }

        m_engine = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: m_height,
            kernels: m_kernels.Value,
            options: new SdfWorldEngineOptions(
                // A filming view never bakes carves (it renders the host world's program, and RequestBrickBake is never
                // called on it), so provisioning the default 64 MB brick pool would waste ~64 MB per view — ~4 GB at the
                // 64-view cap. Capacity 0 gives a 1-float filler; a filmed SampledRegion renders via the shader's
                // conservative uncarved-hull fallback (never a box-shaped hole).
                BrickPoolVoxelCapacity: 0,
                DynamicTransformCapacity: m_dynamicTransformCapacity,
                InstanceCapacity: m_instanceCapacity,
                Program: m_currentProgram,
                ProgramWordCapacity: m_programWordCapacity,
                TimingFactory: timingFactory,
                TimingRecorder: timingRecorder,
                ViewportCapacity: 1
            ),
            width: m_width
        );

        if ((timingFactory is not null) && (timingRecorder is not null)) {
            Console.Error.WriteLine(value: (m_engine.TimingEnabled
                ? $"[view-timing] camera view enabled | period {m_engine.TimingCapabilities.PeriodNanoseconds:0.###}ns"
                : "[view-timing] camera view — the device reports no usable GPU timestamps; running untimed."));
        }
    }

    // Re-uploads the shared world program when the host's revision counter has advanced since the last resolve — a
    // no-op otherwise (mirrors CameraFeedPool.Rebuild).
    private void Rebuild(SdfProgram program, int revision) {
        m_currentProgram = program;

        if ((m_engine is null) || (revision == m_lastUploadedRevision)) {
            return;
        }

        m_engine.UploadProgram(program: program);
        m_lastUploadedRevision = revision;
    }

    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_engine?.Dispose();
        m_engine = null;
        m_lastUploadedRevision = -1;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_engine?.Dispose();
        m_engine = null;
    }
}
