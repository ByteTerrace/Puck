using System.Numerics;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm.Views;

/// <summary>
/// A FULLY nested world — its own <see cref="ISdfFrameSource"/> (typically a small <see cref="SdfCompositionFrameSource"/>
/// with its own emitters, unrelated to the host world's), rendered offscreen through its own
/// <see cref="SdfWorldEngine"/> exactly like <see cref="SdfCameraView"/> films the HOST world. The hypervisor proof:
/// a screen surface wired to this view shows an entirely separate SDF program — a world inside the world — and if
/// THAT world's own emitters include a screen surface wired to yet another nested view, the chain composes (one
/// frame of lag per hop, the same self-reference-safe TV-in-TV rule <see cref="ViewStack"/> enforces for every
/// content kind).
/// </summary>
public sealed class NestedWorldView : IViewContent, IDisposable {
    /// <summary>The view's fixed render width — the native brick panel size (matches <see cref="SdfCameraView.DefaultWidth"/>).</summary>
    public const uint DefaultWidth = 160;
    /// <summary>The view's fixed render height.</summary>
    public const uint DefaultHeight = 144;

    private readonly IServiceProvider m_services;
    private readonly bool m_hostsOnDirectX;
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_width;
    private readonly uint m_height;
    private SdfWorldEngine? m_engine;
    private SdfWorldKernels? m_kernels;

    /// <summary>Wraps a nested world's own frame source as view content.</summary>
    /// <param name="services">The application services (resolves the neutral GPU compute factories).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="frameSource">The nested world's OWN frame source — captured fresh every <see cref="Resolve"/>,
    /// entirely independent of the host world's program/anchors/emitters.</param>
    /// <param name="width">The render width (default the native panel size).</param>
    /// <param name="height">The render height (default the native panel size).</param>
    public NestedWorldView(IServiceProvider services, bool hostsOnDirectX, ISdfFrameSource frameSource, uint width = DefaultWidth, uint height = DefaultHeight) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_frameSource = frameSource;
        m_width = width;
        m_height = height;
    }

    /// <inheritdoc/>
    /// <remarks>Always zero — a nested world films its OWN lit content; it contributes no light to the host room
    /// beyond whatever the host's own screen-surface glow accounting already does for a bound image.</remarks>
    public Vector3 RoomGlow => Vector3.Zero;

    /// <inheritdoc/>
    /// <remarks>Always <see langword="true"/> — a nested-world resolve is a real offscreen render pass (capturing its
    /// own frame source AND submitting it).</remarks>
    public bool IsBudgeted => true;

    /// <inheritdoc/>
    public nint Resolve(in ViewRenderContext context) {
        if (
            (m_services.GetService(serviceType: typeof(IGpuComputeServices)) is not IGpuComputeServices gpu) ||
            !context.Host.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)
        ) {
            return 0;
        }

        var frame = m_frameSource.CaptureFrame(
            width: m_width,
            height: m_height,
            deltaSeconds: (float)context.Host.FrameDeltaSeconds,
            interpolationAlpha: (float)context.Host.InterpolationAlpha
        );

        EnsureEngine(device: device, gpu: gpu, frame: frame);
        m_engine!.SubmitFrame(frame: frame);

        return m_engine.OutputImageViewHandle;
    }

    private void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, SdfFrame frame) {
        if (m_engine is not null) {
            return;
        }

        m_kernels ??= SdfWorldKernels.Load(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: m_hostsOnDirectX));

        // A composed frame source (the common case) already knows its own worst-case envelope; a bare source falls
        // back to measuring its FIRST frame — safe only because a nested world with no probe discipline of its own
        // must therefore never grow past its first frame's shape (documented risk, same as any non-composed
        // ISdfFrameSource used directly against SdfWorldEngine).
        var wordCapacity = ((m_frameSource is SdfCompositionFrameSource composed) ? composed.WorstCaseProgramWordCapacity : frame.Program.Words.Length);
        var instanceCapacity = ((m_frameSource is SdfCompositionFrameSource composedInstances) ? composedInstances.WorstCaseInstanceCapacity : frame.Program.Instances.Count);
        var dynamicCapacity = ((m_frameSource is SdfCompositionFrameSource composedTransforms) ? composedTransforms.WorstCaseDynamicTransformCapacity : frame.DynamicTransforms.Count);

        // GPU performance counters: same live arming as SdfEngineNode.EnsureEngine / SdfCameraView.EnsureEngine —
        // GpuTimingControl.Shared, gated on the backend having registered the timing seam.
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
                // A nested-world filming view never bakes carves (RequestBrickBake is never called on it), so the default
                // 64 MB brick pool would be dead allocation — ~4 GB at the 64-view cap. Capacity 0 gives a 1-float
                // filler; a nested SampledRegion renders via the shader's conservative uncarved-hull fallback.
                BrickPoolVoxelCapacity: 0,
                DynamicTransformCapacity: dynamicCapacity,
                InstanceCapacity: instanceCapacity,
                Program: frame.Program,
                ProgramWordCapacity: wordCapacity,
                TimingFactory: timingFactory,
                TimingRecorder: timingRecorder,
                ViewportCapacity: (uint)Math.Max(val1: 1, val2: frame.Views.Count)
            ),
            width: m_width
        );

        if ((timingFactory is not null) && (timingRecorder is not null)) {
            Console.Error.WriteLine(value: (m_engine.TimingEnabled
                ? $"[view-timing] nested-world view enabled | period {m_engine.TimingCapabilities.PeriodNanoseconds:0.###}ns"
                : "[view-timing] nested-world view — the device reports no usable GPU timestamps; running untimed."));
        }
    }

    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_engine?.Dispose();
        m_engine = null;
    }
}
