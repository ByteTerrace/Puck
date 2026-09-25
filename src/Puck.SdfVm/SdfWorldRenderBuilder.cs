using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>The assembled SDF world render host a <see cref="SdfWorldRenderBuilder.Build"/> call produced.</summary>
/// <param name="Producer">The SDF engine node itself (the runtime seam for debug captures etc.).</param>
/// <param name="Root">The node to produce frames from: a debug-view wrapper over the producer/decorator.</param>
public sealed record SdfWorldRender(
    SdfEngineNode Producer,
    IRenderNode Root
) : ICaptureRequestTarget {
    /// <summary>The outermost decorator's capture capability, when the decorated chain has one — set by
    /// <see cref="SdfWorldRenderBuilder.Build"/> right after building the decorator chain, before it is wrapped for
    /// the caller. <see langword="null"/> when no decorator is present (none specified, or resources absent).</summary>
    internal ICaptureRequestTarget? CaptureTarget { get; init; }

    /// <summary>The path of a capture armed through <see cref="RequestCapture"/> that no frame has served yet, or
    /// <see langword="null"/> when nothing is outstanding. The outermost decorator reports its whole chain (each one
    /// falls through to its inner), so this covers every node between it and <see cref="Producer"/>, which is asked
    /// directly when the chain carries no decorator at all. A caller reports an outstanding path rather than letting
    /// a run end with a requester believing a file exists. Null is not proof of success; await the returned
    /// request's completion to observe the write outcome.</summary>
    public string? PendingCapturePath => (CaptureTarget?.PendingCapturePath ?? Producer.PendingCapturePath);

    /// <summary>Arms a one-shot capture of the NEXT produced frame on the OUTERMOST decorator (the console overlay,
    /// or the binding bar beneath it, whichever wraps the chain) so the readback sees what the player actually
    /// sees — the 2D overlays composite AFTER <see cref="Producer"/>'s own render. Falls back to <see cref="Producer"/>
    /// directly when the chain has no capture-capable decorator, matching the pre-overlay behavior in that case.
    /// The request's completion reports the actual PNG write or failure.</summary>
    /// <param name="request">The unserved request; the caller creates the parent directory of its path.</param>
    /// <exception cref="InvalidOperationException">The render chain already has a pending capture, or the request is
    /// terminal.</exception>
    /// <exception cref="ObjectDisposedException">The serving target has been disposed.</exception>
    public void RequestCapture(FrameCaptureRequest request) {
        if (CaptureTarget is { } target) {
            target.RequestCapture(request: request);
        } else {
            Producer.RequestCapture(request: request);
        }
    }
}
/// <summary>
/// The ONE assembly path from an <see cref="SdfWorldRenderSpec"/> to a runnable SDF world render host. Every
/// backend-specific choice lives here: kernel bytecode selection (SPIR-V vs DXIL) derives from the spec's resolved
/// host backend — a caller never names a bytecode extension. The spec's <c>Decorate</c> seam applies on EVERY
/// backend (decorators are backend-neutral; the caller hands them backend-selected bytecode).
/// </summary>
public static class SdfWorldRenderBuilder {
    /// <summary>Assembles the SDF world render host a spec describes.</summary>
    /// <param name="pipelines">The composition's pipeline cache, forwarded unchanged into the built
    /// <see cref="SdfEngineNode"/>. The factory reads the node's kernels through it
    /// (<see cref="SdfWorldPipelineCache.LoadDeployed"/>).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The assembled producer and root.</returns>
    public static SdfWorldRender Build(SdfWorldPipelineCache pipelines, SdfWorldRenderSpec spec) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(spec);

        // The frame-source decorator seam (SdfWorldRenderSpec.DecorateFrameSource): a host wraps the scene's frame
        // source here (e.g. the overworld's diegetic-UI director, which emits its own SDF geometry into the program)
        // before the engine node is built. Identity when the spec supplies none — most callers (the document-driven
        // world path) never set it.
        var frameSource = ((spec.DecorateFrameSource is { } decorateFrameSource)
            ? decorateFrameSource(spec.FrameSource)
            : spec.FrameSource
        );

        var producer = new SdfEngineNode(
            brickPoolVoxelCapacity: spec.BrickPoolVoxelCapacity,
            children: spec.Children,
            createStorageImage: spec.CreateOutputImage,
            dynamicTransformCapacity: spec.DynamicTransformCapacity,
            frameSource: frameSource,
            height: spec.Height,
            instanceCapacity: spec.InstanceCapacity,
            // The views built over the same cache read this same deployed set, so a boot reads the kernels once.
            kernels: pipelines.LoadDeployed(bytecodeExtension: BytecodeExtension(hostsOnDirectX: spec.HostsOnDirectX)),
            programWordCapacity: spec.ProgramWordCapacity,
            screenSources: spec.ScreenSources,
            screenLights: spec.ScreenLights,
            // Read straight off the frame source (ISdfFrameSource.ScreenSurfaceTransforms, default null) rather than
            // a spec field: this is the ONE place that needs to know the seam exists at all.
            screenSurfaceTransforms: frameSource.ScreenSurfaceTransforms,
            pipelines: pipelines,
            viewportCapacity: spec.ViewportCapacity,
            width: spec.Width
        );

        producer.SetScreenSourceFrames(screenSourceFrames: spec.ScreenSourceFrames);
        var root = ((IRenderNode)producer);

        if (spec.Decorate is { } decorate) {
            root = decorate(producer);
        }

        // Captured BEFORE root is wrapped in SdfWorldRenderRoot below: `root` here is the actual decorated chain
        // (the console overlay wrapping the binding bar wrapping the producer, or a subset/none of that), so this
        // is the true outermost node — the debug-view wrapper adds no capture capability of its own.
        return new SdfWorldRender(
            Producer: producer,
            Root: new SdfWorldRenderRoot(
                inner: root,
                producer: producer
            )
        ) {
            CaptureTarget = (root as ICaptureRequestTarget),
        };
    }
    /// <summary>The kernel bytecode extension for a resolved host backend — the counterpart of the per-child
    /// <c>directX</c> flag (the GamingBrick child node), kept beside it so the two can never drift.</summary>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public static string BytecodeExtension(bool hostsOnDirectX) => ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX);

    private sealed class SdfWorldRenderRoot(SdfEngineNode producer, IRenderNode inner) : IRenderNode, IDebugViewTarget {
        public int DebugMode {
            get => producer.DebugMode;
            set => producer.DebugMode = value;
        }
        public NodeDescriptor Descriptor => inner.Descriptor;

        public void Dispose() => inner.Dispose();
        public void OnDeviceLost() => inner.OnDeviceLost();
        public Surface ProduceFrame(in FrameContext context) => inner.ProduceFrame(context: in context);
    }
}
