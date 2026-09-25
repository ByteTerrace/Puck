using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm.Views;

/// <summary>
/// Renders an SDF world into an offscreen image. A small <see cref="SdfWorldEngine"/> is posed for each resolve by an
/// <see cref="ISdfCameraRig"/> against a
/// live pose an <see cref="ISdfAnchorSource"/> resolves by id — or, for a rig that
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
    /// <summary>The view's fixed render height.</summary>
    public const uint DefaultHeight = 144;
    /// <summary>The view's fixed render width — the native brick panel size, matching every other diegetic screen
    /// source in the overworld (KEEP IN SYNC with the overworld's 160:144 screen authoring).</summary>
    public const uint DefaultWidth = 160;

    private readonly int m_dynamicTransformCapacity;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly int m_instanceCapacity;
    private readonly int m_programWordCapacity;
    private readonly uint m_width;

    // Owned here rather than by the engine, so submission identities keep increasing across an engine rebuild.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: SdfWorldEngine.FrameRingSize,
        name: "gpu.camera-view"
    );

    private SdfProgram? m_currentProgram;
    private SdfWorldEngine? m_engine;
    private Func<IGpuDeviceContext, IGpuImage>? m_exportFactory;

    // Built off the frame thread on the first resolve and kept across engine rebuilds (an export-factory change) until
    // a device loss or disposal.
    private readonly SdfWorldPipelineSource m_pipelines;

    private int m_lastUploadedRevision = -1;

    // Export-mode changes rebuild the engine, but ViewStack keeps serving the last resolved image handle until a
    // replacement frame completes. Keep the engine backing that handle alive across the rebuild; disposing it in
    // ExportFactory's setter would leave a wired screen sampling a released image during a budgeted refresh gap.
    private SdfWorldEngine? m_retiredEngine;

    /// <summary>Initializes a camera view against the host's worst-case capacity envelope, so this view's own program
    /// upload never throws when the shared program grows within that ceiling (same contract as
    /// <c>CameraFeedPool</c>'s constructor).</summary>
    /// <param name="pipelines">The composition's pipeline cache the view leases its engine's pipeline set from; the
    /// engine records through the services of the device each resolve renders on.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <param name="width">The render width (default the native panel size).</param>
    /// <param name="height">The render height (default the native panel size).</param>
    public SdfCameraView(SdfWorldPipelineCache pipelines, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity, uint width = DefaultWidth, uint height = DefaultHeight) {
        ArgumentNullException.ThrowIfNull(pipelines);

        m_pipelines = new SdfWorldPipelineSource(cache: pipelines);
        m_hostsOnDirectX = hostsOnDirectX;
        m_programWordCapacity = programWordCapacity;
        m_instanceCapacity = instanceCapacity;
        m_dynamicTransformCapacity = dynamicTransformCapacity;
        m_width = width;
        m_height = height;
    }

    /// <summary>Gets or sets the delegate that supplies this view's anchor id every frame — a delegate rather than a
    /// cached int so a subject that changes (a camera following a different entity) is re-read rather than sticking to
    /// a stale id.</summary>
    public Func<int>? AnchorIdSource { get; set; }
    /// <summary>Resolves this view's anchor id every frame (see <see cref="AnchorSource"/>) — null or a resolved id
    /// that fails to resolve leaves the rig's <c>anchor</c> parameter at <see langword="default"/> (a
    /// <see cref="FixedRig"/> ignores it regardless).</summary>
    public ISdfAnchorSource? AnchorSource { get; set; }
    /// <summary>Whether this offscreen camera skips ambient occlusion. Defaults to false; low-resolution diegetic
    /// displays may opt in independently of the host world's lighting quality.</summary>
    public bool DisableAmbientOcclusion { get; set; }
    /// <summary>Whether this offscreen camera skips soft shadows. Defaults to false; low-resolution diegetic displays
    /// may opt in independently of the host world's lighting quality.</summary>
    public bool DisableSoftShadows { get; set; }
    /// <summary>Ends a successful <see cref="TryBeginExportWrite"/> reservation after submission, reporting whether
    /// the export queue reached a complete frame.</summary>
    public Action<bool>? EndExportWrite { get; set; }
    /// <summary>The output-image factory forwarded to <see cref="SdfWorldEngineOptions.CreateOutputImage"/> —
    /// <see langword="null"/> (the default) builds a plain same-device image; a factory returning an
    /// <see cref="IGpuExportableImage"/> puts the engine in export mode (see <see cref="ExportSharedHandle"/>).
    /// Only consulted while building a new engine (<see cref="EnsureEngine"/> is a no-op once one exists), so setting
    /// this after the engine already exists retires it — the next <see cref="Resolve"/> rebuilds against the new
    /// factory, while the old engine stays alive until that replacement frame completes (a fresh engine also means
    /// a fresh <see cref="SdfWorldEngine.ExportSharedHandle"/>).</summary>
    public Func<IGpuDeviceContext, IGpuImage>? ExportFactory {
        get => m_exportFactory;
        set {
            if (ReferenceEquals(
                objA: m_exportFactory,
                objB: value
            )) {
                return;
            }

            m_exportFactory = value;

            if (m_engine is not null) {
                if (m_retiredEngine is null) {
                    m_retiredEngine = m_engine;
                } else {
                    // A second factory change before the pending replacement completes cannot make this unserved
                    // engine the ViewStack's cached image; the first retired engine still owns that handle.
                    m_engine.Dispose();
                }

                m_engine = null;
                m_lastUploadedRevision = -1;
                m_work.Invalidate();
            }
        }
    }
    /// <summary>Gets an identity that changes every time the underlying engine (and so its exported image) is
    /// rebuilt — a fresh <see cref="SdfWorldEngine"/> instance on every <see cref="ExportFactory"/> change, device
    /// loss, or dimension recreation. <see langword="null"/> while no engine exists.</summary>
    public object? ExportGeneration => m_engine;
    /// <summary>Gets the live engine's exported shared handle (see <see cref="SdfWorldEngine.ExportSharedHandle"/>),
    /// or 0 while <see cref="ExportFactory"/> is unset or no engine has been built yet.</summary>
    public nint ExportSharedHandle => (m_engine?.ExportSharedHandle ?? 0);
    /// <inheritdoc/>
    /// <remarks>Always <see langword="true"/> — a camera resolve is a real offscreen render pass.</remarks>
    public bool IsBudgeted => true;
    /// <summary>The rig that poses this camera from its resolved anchor. Null resolves to no signal (0) — a view
    /// registered before its rig is known (should not normally happen; every constructor path assigns one before
    /// first <see cref="Resolve"/>).</summary>
    public ISdfCameraRig? Rig { get; set; }
    /// <inheritdoc/>
    /// <remarks>Always zero — a camera FILMS an already-lit world; it is not itself a light source (matches
    /// <c>CameraFeedPool</c>'s camera feeds, which reported no light of their own).</remarks>
    public Vector3 RoomGlow => Vector3.Zero;
    /// <summary>Optional cross-device write reservation used by an exported-image consumer. When present,
    /// <see cref="Resolve"/> keeps the last completed image instead of overwriting it while a consumer holds a read
    /// lease.</summary>
    public Func<bool>? TryBeginExportWrite { get; set; }
    /// <summary>Gets the GPU work this view's offscreen engine recorded, per pass, for its newest completed submission
    /// (see <see cref="SdfWorldEngine.Work"/>). Submission identities keep increasing when the view rebuilds its engine;
    /// the view's work reads unavailable after a rebuild until a frame of the new engine completes.</summary>
    public IGpuWorkSource Work => m_work;
    /// <summary>Gets the GPU objects this view's engines have created, over the view's whole life.</summary>
    public IWorkCounterSource WorkLifetime => m_work;

    // Builds the engine once its pipelines are ready; false while they build on the thread pool.
    private bool EnsureEngine(IGpuDeviceContext device, SdfProgram program) {
        if (m_engine is not null) {
            return true;
        }

        if (m_pipelines.Poll(
            device: device,
            hostsOnDirectX: m_hostsOnDirectX,
            includeBrickPipelines: false,
            kernels: null
        ) is not { } pipelines) {
            return false;
        }

        m_currentProgram ??= program;

        var engineOptions = new SdfWorldEngineOptions(
            // A filming view never bakes carves (it renders the host world's program, and RequestBrickBake is never
            // called on it), so provisioning the default 64 MB brick pool would waste ~64 MB per view — ~4 GB at the
            // 64-view cap. Capacity 0 gives a 1-float filler; a filmed SampledRegion renders via the shader's
            // conservative uncarved-hull fallback (never a box-shaped hole).
            BrickPoolVoxelCapacity: 0,
            CreateOutputImage: m_exportFactory,
            DynamicTransformCapacity: m_dynamicTransformCapacity,
            InstanceCapacity: m_instanceCapacity,
            Program: m_currentProgram,
            ProgramWordCapacity: m_programWordCapacity,
            ViewportCapacity: 1,
            WorkLedger: m_work
        );

        SdfWorldEngine.CheckAdmission(
            device: device,
            options: engineOptions,
            pipelines: pipelines
        );
        m_engine = new SdfWorldEngine(
            device: device,
            height: m_height,
            options: engineOptions,
            pipelines: pipelines,
            width: m_width
        );

        return true;
    }
    // Re-uploads the shared world program when the host's revision counter has advanced since the last resolve — a
    // no-op otherwise (mirrors CameraFeedPool.Rebuild).
    private void Rebuild(SdfProgram program, int revision) {
        m_currentProgram = program;

        if (
            (m_engine is null) ||
            (revision == m_lastUploadedRevision)
        ) {
            return;
        }

        m_engine.UploadProgram(program: program);
        m_lastUploadedRevision = revision;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_engine?.Dispose();
        m_engine = null;
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;
        m_pipelines.Release();
    }
    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_engine?.Dispose();
        m_engine = null;
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;
        m_pipelines.Release();
        m_lastUploadedRevision = -1;
        m_work.Invalidate();
    }
    /// <inheritdoc/>
    public nint Resolve(in ViewRenderContext context) {
        if (
            (Rig is not { } rig) ||
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
            if (
                (AnchorIdSource?.Invoke() is not { } anchorId) ||
                !source.TryResolveAnchor(
                anchor: out anchor,
                anchorId: anchorId
            )
            ) {
                return 0;
            }
        }

        // No signal while the pipelines build — or the image an export-factory rebuild is replacing, still backed by
        // its retired engine.
        if (!EnsureEngine(
            device: device,
            program: context.Program
        )) {
            return (m_retiredEngine?.OutputImageViewHandle ?? 0);
        }

        Rebuild(
            program: context.Program,
            revision: context.ProgramRevision
        );

        var clock = new SdfCameraClock(
            PresentationSeconds: context.Time,
            AuthoritativeTick: context.AuthoritativeTick
        );

        var (eye, target, fovRadians) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        for (var screenIndex = 0; (screenIndex < SdfProgramBuilder.MaxScreenSurfaces); screenIndex++) {
            m_engine!.SetScreenSource(
                screenIndex: screenIndex,
                imageViewHandle: context.ResolveScreenSource(arg: screenIndex)
            );
        }

        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: fovRadians,
            position: eye,
            target: target,
            viewportHeight: m_height,
            viewportWidth: m_width
        );
        // Derived from the frame the room is rendering, never rebuilt beside it. Every per-frame lever the
        // host set — the far-field isolators, the shadow-march and AO mode selectors, the sun-disc sample
        // index, the lighting — reaches this offscreen render by construction, and only what this view
        // genuinely owns is overridden below. A fresh `new SdfFrame(...)` would silently drop any host lever
        // added to SdfFrame later; `with` cannot forget a member that did not exist when it was written.
        var frame = context.HostFrame with {
            // This view's own eye, filling this view's own render target.
            Views = [new SdfViewSnapshot(
                Camera: camera,
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            )],
            // This view's own upload bookkeeping: its offscreen engine re-uploads on ITS revision watch (Rebuild
            // above), so the host's ProgramChanged is never the right answer for this engine.
            Program = m_currentProgram!,
            ProgramChanged = false,
            // A view may only ADD cost restrictions, never lift them. A 160x144 diegetic panel opts OUT of shadows and
            // AO the room can afford (see WorldScreenBinder.RegisterCameraView, which sets both), but must never opt
            // INTO quality the room itself has switched off — that would make the jumbotron cost more than the world
            // it films, and would let a view ignore a host lever in the one direction that costs rather than saves.
            DisableAmbientOcclusion = (context.HostFrame.DisableAmbientOcclusion || DisableAmbientOcclusion),
            DisableSoftShadows = (context.HostFrame.DisableSoftShadows || DisableSoftShadows),
        };

        var tryBeginExportWrite = TryBeginExportWrite;
        var endExportWrite = EndExportWrite;
        var reservedExportWrite = (tryBeginExportWrite?.Invoke() ?? true);

        if (!reservedExportWrite) {
            // A factory change may already have built the replacement engine while a foreign reader still leases
            // the old export. The replacement has not rendered yet, so keep returning the image ViewStack already
            // caches instead of publishing an uninitialized output handle.
            return (m_retiredEngine?.OutputImageViewHandle ?? m_engine!.OutputImageViewHandle);
        }

        var exportWriteCompleted = false;

        try {
            m_engine!.SubmitFrame(frame: frame);
            exportWriteCompleted = true;
        } finally {
            if (tryBeginExportWrite is not null) {
                endExportWrite?.Invoke(exportWriteCompleted);
            }
        }

        var outputImageViewHandle = m_engine.OutputImageViewHandle;

        // The replacement is now the result this Resolve publishes. Disposing drains the device, covering earlier
        // in-flight frames that may still sample the previously cached image.
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;

        // OutputImageViewHandle stays a valid same-device view even in export mode (IGpuExportableImage IS an
        // IGpuImage) — a jumbotron sampling this view and a probe kernel importing ExportSharedHandle read the
        // same drained frame through two different handles.
        return outputImageViewHandle;
    }
}
