using Puck.Abstractions.Gpu;

namespace Puck.SdfVm.Views;

// The lazy engine-creation shape every filming view (a view that renders a world composition but never bakes a
// carve into its own brick pool — SdfCameraView, WorldSessionView) shares: capacity 0 gives a 1-float filler pool
// and the shader's conservative uncarved-hull fallback, so a filming view's pool never wastes real memory on carves
// it never bakes.
internal static class SdfFilmingViewEngine {
    /// <summary>Builds <paramref name="engine"/> once <paramref name="pipelines"/> is ready and does nothing once it
    /// exists. The first call starts the pipeline build off the frame thread.</summary>
    /// <param name="device">The GPU device context, whose services the engine records through.</param>
    /// <param name="frame">This call's frame, read for its worst-case capacities when <paramref name="frameSource"/>
    /// is not a composition (see the composed-vs-bare capacity fallback below).</param>
    /// <param name="frameSource">The view's frame source; a <c>SdfCompositionFrameSource</c> already knows its own
    /// worst-case envelope, a bare source falls back to measuring its first frame.</param>
    /// <param name="hostsOnDirectX">Whether the view's window hosts on Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="height">The view's render height.</param>
    /// <param name="width">The view's render width.</param>
    /// <param name="engine">The view's cached engine; left unchanged if already built.</param>
    /// <param name="pipelines">The view's lease holder on the shared pipeline set, which takes the lease on the deployed
    /// kernels off the frame thread.</param>
    /// <param name="work">The view's work ledger, which the engine counts into.</param>
    /// <returns><see langword="true"/> when <paramref name="engine"/> exists; <see langword="false"/> while the
    /// pipelines build, or when the build was refused, which is retried only once its inputs (the engine options this
    /// frame asks for, the device or the pipeline set) change.</returns>
    public static bool EnsureEngine(IGpuDeviceContext device, SdfFrame frame, ISdfFrameSource frameSource, bool hostsOnDirectX, uint height, uint width, GpuWorkLedger work, SdfWorldPipelineSource pipelines, ref SdfWorldEngine? engine) {
        if (engine is not null) {
            return true;
        }

        engine = pipelines.TryBuild(
            construct: static (ready, regionCopy, inputs) => new SdfWorldEngine(
                device: inputs.Device,
                height: inputs.Height,
                options: inputs.Options,
                pipelines: ready,
                regionCopy: regionCopy,
                width: inputs.Width
            ),
            device: device,
            hostsOnDirectX: hostsOnDirectX,
            includeBrickPipelines: false,
            inputsOf: static state => (
                state.Device,
                state.Height,
                state.Width,
                Options: Options(
                    frame: state.Frame,
                    frameSource: state.FrameSource,
                    work: state.Work
                )
            ),
            kernels: null,
            label: "session-view",
            state: (Device: device, Frame: frame, FrameSource: frameSource, Height: height, Width: width, Work: work)
        );

        return (engine is not null);
    }

    private static SdfWorldEngineOptions Options(SdfFrame frame, ISdfFrameSource frameSource, GpuWorkLedger work) {
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

        return new SdfWorldEngineOptions(
            BrickPoolVoxelCapacity: 0,
            DynamicTransformCapacity: dynamicCapacity,
            InstanceCapacity: instanceCapacity,
            Program: frame.Program,
            ProgramWordCapacity: wordCapacity,
            ViewportCapacity: ((uint)Math.Max(
                val1: 1,
                val2: frame.Views.Count
            )),
            WorkLedger: work
        );
    }
}
