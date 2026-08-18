using System.Diagnostics;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm.Views;

/// <summary>
/// A session-observed world's independent render — the successor <see cref="NestedWorldView"/>'s own remarks name
/// ("reuse only after those gaps close; a successor is acceptable"), built for docs/vision.md's "Observation
/// and display" section. Wraps an independent <see cref="ISdfFrameSource"/> (a
/// <see cref="SdfCompositionFrameSource"/> composing a destination's own static geometry) through its own offscreen
/// <see cref="SdfWorldEngine"/>, exactly like <see cref="NestedWorldView"/> — the difference that matters is how it
/// captures: never through the host's own clock.
/// </summary>
/// <remarks>
/// <para><b>Timing, contrasted with <see cref="NestedWorldView"/>.</b> That type's own remarks name its gap
/// plainly: it "uses the host presentation clock" — <c>context.Host.FrameDeltaSeconds</c>/<c>InterpolationAlpha</c>
/// — which docs/vision.md's "Ruled out" table rejects outright ("Host interpolation for destination views":
/// independently scheduled or remote worlds do not share a presentation coordinate). This type instead measures
/// its own produced-frame interval — real wall time between this view's own <see cref="Resolve"/> calls, zero on
/// the first — and hands that to <see cref="ISdfFrameSource.CaptureFrame"/>'s <c>deltaSeconds</c>: never the
/// host's clock (this view's own produce cadence, not the host's per-frame one), so a dressed frame's own
/// time-based ease (a chase-camera <c>SmoothRate</c>, an accumulated presentation clock) advances across produced
/// frames, including the ones this view's round-robin turn skips. <c>interpolationAlpha</c> stays fixed at zero:
/// the wrapped content carries no live-body pose to interpolate through this general seam (see
/// <c>Puck.World.Client.WorldSessionMirror</c>'s own staged-boundary remarks) — the away-seat dresser derives its
/// own alpha independently, from the mirror's snapshot-arrival timestamp, never from this parameter.</para>
/// <para>Budgeted like <see cref="NestedWorldView"/> and <see cref="SdfCameraView"/> (<see cref="IsBudgeted"/> is
/// <see langword="true"/>): <see cref="ViewStack"/>'s own refresh-divisor/round-robin budget and its
/// persisted-last-handle-on-skip contract are what satisfy "retain the last completed image when the budget skips a
/// refresh" — nothing here duplicates that.</para>
/// </remarks>
public sealed class WorldSessionView : IViewContent, IDisposable {
    /// <summary>The view's fixed render height.</summary>
    public const uint DefaultHeight = 144;
    /// <summary>The view's fixed render width — the native brick panel size (matches <see cref="SdfCameraView.DefaultWidth"/>).</summary>
    public const uint DefaultWidth = 160;

    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly bool m_isBudgeted;
    private readonly Func<int, nint>? m_resolveScreenSource;
    private readonly SdfViewGpuServices m_services;
    private readonly uint m_width;

    // H3: suppresses re-narrating an upload/capacity fault every produced frame while it keeps recurring (the
    // rebuilt engine re-probes the SAME frame source, so it fails again immediately until the emitter-side re-probe
    // gap this type's own remarks name is closed) — cleared the moment a Resolve actually completes, so a NEW,
    // distinct fault after a period of success narrates again rather than staying silenced forever.
    private bool m_capacityFaultNarrated;
    private SdfWorldEngine? m_engine;
    private bool m_hasProduced;
    private SdfWorldKernels? m_kernels;
    // H3: the last successfully produced frame's output handle — served while m_engine is torn down and awaiting
    // rebuild (see Resolve's catch below), and while no frame has ever completed (0, the ordinary "no signal" value).
    private nint m_lastGoodHandle;
    // This view's own produced-frame clock (see the type remarks' "fix over NestedWorldView"): the wall-clock
    // timestamp of this view's own last Resolve, and whether one has happened yet — never the host's per-frame
    // clock, and never advanced on a frame this view's own round-robin turn skipped (Resolve simply is not called
    // on those).
    private long m_lastProduceTimestamp;
    // H3: the faulted engine whose storage image BACKS m_lastGoodHandle — kept alive (not disposed) while that
    // handle is the served result, because SdfWorldEngine.Dispose destroys the image behind it and the ViewStack
    // keeps compositing the handle until a replacement completes. Disposed the moment a replacement engine finishes
    // a frame (the served result moves), on device loss (the handle is dead either way), and on this view's own
    // disposal.
    private SdfWorldEngine? m_retiredEngine;

    /// <summary>Wraps a session-observed world's own frame source as view content.</summary>
    /// <param name="services">The concrete GPU-services closure this view forwards to its offscreen engine.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="frameSource">The destination's OWN frame source — entirely independent of the host world's
    /// program/anchors/emitters.</param>
    /// <param name="width">The render width (default the native panel size).</param>
    /// <param name="height">The render height (default the native panel size).</param>
    /// <param name="resolveScreenSource">Optional first-level screen-source resolver for this projection. Omit it
    /// to bind every screen surface to no-signal, which is also the recursion boundary for child projections.</param>
    /// <param name="isBudgeted">Whether this view counts against <see cref="OffscreenRenderBudget.PerProducedFrame"/>'s
    /// round-robin share (the default, <see langword="true"/> — an ordinary camera-projection session, tolerant of a stale image
    /// between refreshes). <see langword="false"/> for a WINDOW projection: a stale image would show the destination
    /// scene lagging the viewer's own eye movement, breaking the parallax the projection exists for, so it must
    /// resolve every produced frame instead (see <see cref="IsBudgeted"/>). The document-level ceiling on how many
    /// simultaneously unbudgeted sessions may exist is the same <see cref="OffscreenRenderBudget.PerProducedFrame"/>,
    /// enforced by the host's document validator — this constructor trusts its caller, not a second count here.</param>
    public WorldSessionView(SdfViewGpuServices services, bool hostsOnDirectX, ISdfFrameSource frameSource, uint width = DefaultWidth, uint height = DefaultHeight, Func<int, nint>? resolveScreenSource = null, bool isBudgeted = true) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_frameSource = frameSource;
        m_width = width;
        m_height = height;
        m_resolveScreenSource = resolveScreenSource;
        m_isBudgeted = isBudgeted;
    }

    /// <inheritdoc/>
    /// <remarks>See the <c>isBudgeted</c> constructor parameter — <see langword="false"/> for a window projection,
    /// <see langword="true"/> (the constructor default, today's unchanged behavior) for every other session.</remarks>
    public bool IsBudgeted => m_isBudgeted;
    /// <inheritdoc/>
    /// <remarks>Always zero — a session view films its OWN already-lit content; it contributes no light to the host
    /// room beyond whatever the host's own screen-surface glow accounting already does for a bound image.</remarks>
    public Vector3 RoomGlow => Vector3.Zero;

    private void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, SdfFrame frame) =>
        SdfFilmingViewEngine.EnsureEngine(
            device: device,
            engine: ref m_engine,
            frame: frame,
            frameSource: m_frameSource,
            gpu: gpu,
            height: m_height,
            hostsOnDirectX: m_hostsOnDirectX,
            kernels: ref m_kernels,
            services: m_services,
            viewLabel: "session view",
            width: m_width
        );

    /// <inheritdoc/>
    public void Dispose() {
        m_engine?.Dispose();
        m_engine = null;
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;
    }
    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;
        // The cached handle belonged to the now-lost device — never re-served (mirrors ViewStack.NotifyDeviceLost's
        // own LastHandle reset for every entry).
        m_lastGoodHandle = 0;
        // The real wall-clock gap a device-lost rebuild takes must not land as one giant smoothing delta on the
        // first frame after recovery — reseed exactly like a fresh view's first produce.
        m_hasProduced = false;
    }
    /// <inheritdoc/>
    /// <remarks><para><b>Live re-upload (closed).</b> This type's own <see cref="Resolve"/> now re-uploads
    /// <c>frame.Program</c> whenever <see cref="SdfFrame.ProgramChanged"/> reports it changed — the same
    /// <c>ProgramChanged</c>-gated <c>UploadProgram</c> call <see cref="SdfEngineNode"/>'s live-node path already
    /// makes, and the parity <see cref="SdfCameraView.Resolve"/>'s own <c>Rebuild</c>/<c>ProgramRevision</c> check
    /// establishes for a camera view's shared host program. A destination mutation the composed frame source folds
    /// into a rebuilt <c>SdfProgram</c> — an avatar joining (<c>WorldAvatarCatalog.Emit</c> skips an inactive
    /// avatar's instance entirely, so it does not exist in the program until this upload runs), a placement change —
    /// now reaches this view's own offscreen engine the same produced frame the dresser reports it in.</para>
    /// <para><b>Capacity-growth recovery.</b> <see cref="EnsureEngine"/> probes its worst-case capacity from
    /// <c>m_frameSource</c> exactly once — the first time it builds an engine — and the destination's session-scene
    /// emitter (<c>Puck.World.Client.WorldSessionSceneEmitter</c>, composed into <c>m_frameSource</c> by the binder
    /// that constructs this view) itself derives that worst case from the destination's live definition only at
    /// bind time; it does not yet re-probe when a later live mutation grows the destination's geometry (that
    /// re-probe belongs to a fuller fix elsewhere, not this type). Both <see cref="SdfWorldEngine.UploadProgram"/>
    /// and <see cref="SdfWorldEngine.SubmitFrame"/> document this failure mode on themselves
    /// (<c>&lt;exception cref="ArgumentException"&gt;</c>) for a program/frame that outgrew what the engine was
    /// constructed with — now a live call shape (the upload above runs the moment a rebuild happens), not merely a
    /// documented one. The catch below holds the last completed image, narrates once, and retires the engine —
    /// kept alive, not disposed, because the held image is backed by its storage image (see
    /// <c>m_retiredEngine</c>) — so the next <see cref="Resolve"/> lazily rebuilds through the same
    /// <see cref="EnsureEngine"/> path — probing the current (grown) frame source capacity — that built it the
    /// first time, so a capacity fault self-heals one frame after the growth without ever unwinding into
    /// <see cref="ViewStack.RenderFrame"/>; the replacement's first completed frame is what finally releases the
    /// retired engine.</para></remarks>
    public nint Resolve(in ViewRenderContext context) {
        if (!context.Host.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            return 0;
        }

        // This view's own produced-frame interval — real elapsed time since this view's OWN last Resolve, never
        // context.Host.FrameDeltaSeconds (see this type's own remarks). Seeded zero on the first produce so a
        // downstream smoother never sees a spurious first-frame jump.
        // Wall-clock, presentation-only: away-seat framing is not reproducible run-to-run; deterministic capture
        // would need an injected clock here instead of Stopwatch.
        var produceTimestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = (m_hasProduced
            ? (float)Stopwatch.GetElapsedTime(
                endingTimestamp: produceTimestamp,
                startingTimestamp: m_lastProduceTimestamp
            ).TotalSeconds
            : 0f
        );

        m_lastProduceTimestamp = produceTimestamp;
        m_hasProduced = true;

        var frame = m_frameSource.CaptureFrame(
            deltaSeconds: deltaSeconds,
            height: m_height,
            interpolationAlpha: 0f,
            width: m_width
        );

        EnsureEngine(
            device: device,
            gpu: m_services.Gpu,
            frame: frame
        );

        // Matches SdfCameraView.Resolve's own per-frame contract: every screen-surface slot is bound explicitly.
        // Most session projections deliberately carry no nested sources; traveler-follow may supply a resolver for
        // the followed world's first-level session screens while its child projections keep this null, making the
        // depth-1 recursion boundary structural rather than recursive registration.
        for (var screenIndex = 0; (screenIndex < SdfProgramBuilder.MaxScreenSurfaces); screenIndex++) {
            m_engine!.SetScreenSource(
                screenIndex: screenIndex,
                imageViewHandle: (m_resolveScreenSource?.Invoke(arg: screenIndex) ?? 0)
            );
        }

        try {
            // Re-uploads the composed program to this view's own offscreen engine whenever frame.ProgramChanged
            // (WorldSessionSceneEmitter.Dress reference-compares against its own last-dressed program) — mirrors
            // SdfEngineNode's live-node re-upload pattern for the same signal.
            if (frame.ProgramChanged) {
                m_engine!.UploadProgram(program: frame.Program);
            }

            m_engine!.SubmitFrame(frame: frame);
        } catch (ArgumentException exception) {
            if (!m_capacityFaultNarrated) {
                Console.Error.WriteLine(value: $"[session-view] engine capacity exceeded (the destination's live geometry outgrew the capacity this view was bound with) — holding the last image and rebuilding at the current capacity next frame: {exception.Message}");
                m_capacityFaultNarrated = true;
            }

            if (
                (0 != m_lastGoodHandle) &&
                (m_engine!.OutputImageViewHandle == m_lastGoodHandle)
            ) {
                // The served image lives in THIS engine — disposing it here would destroy the storage image the
                // ViewStack keeps compositing (see m_retiredEngine). Retire it instead; the replacement's first
                // completed frame below is what finally releases it.
                m_retiredEngine?.Dispose();
                m_retiredEngine = m_engine;
            } else {
                // This engine never completed a frame (a rebuilt engine re-faulting) — nothing serves its image.
                m_engine!.Dispose();
            }

            m_engine = null;

            return m_lastGoodHandle;
        }

        m_capacityFaultNarrated = false;
        m_lastGoodHandle = m_engine!.OutputImageViewHandle;

        // The replacement has rendered and its handle is this frame's result — the retired image is no longer
        // served anywhere ahead of this return (SdfWorldEngine.Dispose drains the device, covering prior in-flight
        // frames that still sample it).
        m_retiredEngine?.Dispose();
        m_retiredEngine = null;

        return m_lastGoodHandle;
    }
}
