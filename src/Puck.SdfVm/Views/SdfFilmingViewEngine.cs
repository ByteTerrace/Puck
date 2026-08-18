using Puck.Abstractions.Gpu;

namespace Puck.SdfVm.Views;

// The lazy engine-creation shape every filming view (a view that renders a world composition but never bakes a
// carve into its own brick pool — NestedWorldView, WorldSessionView) shares: capacity 0 gives a 1-float filler pool
// and the shader's conservative uncarved-hull fallback, so a filming view's pool never wastes real memory on carves
// it never bakes.
internal static class SdfFilmingViewEngine {
    /// <summary>Builds <paramref name="engine"/> on first call and does nothing on every later call.</summary>
    /// <param name="device">The GPU device context.</param>
    /// <param name="gpu">The GPU compute services.</param>
    /// <param name="frame">This call's frame, read for its worst-case capacities when <paramref name="frameSource"/>
    /// is not a composition (see the composed-vs-bare capacity fallback below).</param>
    /// <param name="frameSource">The view's frame source; a <c>SdfCompositionFrameSource</c> already knows its own
    /// worst-case envelope, a bare source falls back to measuring its first frame.</param>
    /// <param name="services">The view's GPU services (timing factory/recorder).</param>
    /// <param name="hostsOnDirectX">Whether the view's window hosts on Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="height">The view's render height.</param>
    /// <param name="width">The view's render width.</param>
    /// <param name="viewLabel">The view kind, for the timing-arming console line (e.g. <c>"nested-world view"</c>).</param>
    /// <param name="engine">The view's cached engine; left unchanged if already built.</param>
    /// <param name="kernels">The view's cached kernel bytecode; loaded on first call.</param>
    public static void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, SdfFrame frame, ISdfFrameSource frameSource, SdfViewGpuServices services, bool hostsOnDirectX, uint height, uint width, string viewLabel, ref SdfWorldEngine? engine, ref SdfWorldKernels? kernels) {
        if (engine is not null) {
            return;
        }

        kernels ??= SdfWorldKernels.Load(bytecodeExtension: SdfWorldRenderBuilder.BytecodeExtension(hostsOnDirectX: hostsOnDirectX));

        var wordCapacity = ((frameSource is SdfCompositionFrameSource composed)
            ? composed.WorstCaseProgramWordCapacity
            : frame.Program.Words.Length
        );
        var instanceCapacity = ((frameSource is SdfCompositionFrameSource composedInstances)
            ? composedInstances.WorstCaseInstanceCapacity
            : frame.Program.Instances.Count
        );
        var dynamicCapacity = ((frameSource is SdfCompositionFrameSource composedTransforms)
            ? composedTransforms.WorstCaseDynamicTransformCapacity
            : frame.DynamicTransforms.Count
        );

        // GPU performance counters: same live arming as SdfEngineNode.EnsureEngine / SdfCameraView.EnsureEngine —
        // GpuTimingControl.Shared, gated on the backend having registered the timing seam. A known wart: the timing
        // bundle is resolved eagerly at the composition root regardless of arming state, but this engine only picks
        // it up when ViewTiming.Enabled is true at this call (once per engine lifetime) — a view whose engine builds
        // with timing off never gains it until a device-lost rebuild re-runs EnsureEngine.
        var timingFactory = (ViewTiming.Enabled
            ? services.TimingFactory
            : null
        );
        var timingRecorder = (ViewTiming.Enabled
            ? services.TimingRecorder
            : null
        );

        engine = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: height,
            kernels: kernels.Value,
            options: new SdfWorldEngineOptions(
                BrickPoolVoxelCapacity: 0,
                DynamicTransformCapacity: dynamicCapacity,
                InstanceCapacity: instanceCapacity,
                Program: frame.Program,
                ProgramWordCapacity: wordCapacity,
                TimingFactory: timingFactory,
                TimingRecorder: timingRecorder,
                ViewportCapacity: ((uint)Math.Max(
                    val1: 1,
                    val2: frame.Views.Count
                ))
            ),
            width: width
        );

        if (
            (timingFactory is not null) &&
            (timingRecorder is not null)
        ) {
            Console.Error.WriteLine(value: (engine.TimingEnabled
                ? $"[view-timing] {viewLabel} enabled | period {engine.TimingCapabilities.PeriodNanoseconds:0.###}ns"
                : $"[view-timing] {viewLabel} — the device reports no usable GPU timestamps; running untimed."));
        }
    }
}
