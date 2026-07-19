using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>The assembled SDF world render host a <see cref="SdfWorldRenderBuilder.Build"/> call produced.</summary>
/// <param name="Producer">The SDF engine node itself (the runtime seam for debug captures etc.).</param>
/// <param name="Root">The node to produce frames from: a debug-view wrapper over the producer/decorator.</param>
public sealed record SdfWorldRender(
    SdfEngineNode Producer,
    IRenderNode Root
) {
    /// <summary>The outermost decorator's capture capability, when the decorated chain has one — set by
    /// <see cref="SdfWorldRenderBuilder.Build"/> right after building the decorator chain, before it is wrapped for
    /// the caller. <see langword="null"/> when no decorator is present (none specified, or resources absent).</summary>
    internal ICaptureRequestTarget? CaptureTarget { get; init; }

    /// <summary>Arms a one-shot capture of the NEXT produced frame on the OUTERMOST decorator (the console overlay,
    /// or the binding bar beneath it, whichever wraps the chain) so the readback sees what the player actually
    /// sees — the 2D overlays composite AFTER <see cref="Producer"/>'s own render. Falls back to <see cref="Producer"/>
    /// directly when the chain has no capture-capable decorator, matching the pre-overlay behavior in that case.
    /// Callers outside this file never need to name <see cref="ICaptureRequestTarget"/> themselves.</summary>
    /// <param name="path">The PNG path to write; the caller creates the parent directory.</param>
    public void RequestCapture(string path) {
        if (CaptureTarget is { } target) {
            target.RequestCapture(path: path);
        } else {
            Producer.RequestCapture(path: path);
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
    /// <summary>The kernel bytecode extension for a resolved host backend — the counterpart of the per-child
    /// <c>directX</c> flag (the GamingBrick child node), kept beside it so the two can never drift.</summary>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public static string BytecodeExtension(bool hostsOnDirectX) => (hostsOnDirectX ? ".dxil" : ".spv");

    /// <summary>Assembles the SDF world render host a spec describes.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The assembled producer and root.</returns>
    public static SdfWorldRender Build(IServiceProvider serviceProvider, SdfWorldRenderSpec spec) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(spec);

        // The frame-source decorator seam (SdfWorldRenderSpec.DecorateFrameSource): a host wraps the scene's frame
        // source here (e.g. the overworld's diegetic-UI director, which emits its own SDF geometry into the program)
        // before the engine node is built. Identity when the spec supplies none — most callers (the document-driven
        // world path) never set it.
        var frameSource = ((spec.DecorateFrameSource is { } decorateFrameSource) ? decorateFrameSource(spec.FrameSource) : spec.FrameSource);

        var producer = new SdfEngineNode(
            brickPoolVoxelCapacity: spec.BrickPoolVoxelCapacity,
            capturePath: spec.CapturePath,
            children: spec.Children,
            createStorageImage: spec.CreateOutputImage,
            dynamicTransformCapacity: spec.DynamicTransformCapacity,
            frameSource: frameSource,
            height: spec.Height,
            instanceCapacity: spec.InstanceCapacity,
            kernels: SdfWorldKernels.Load(bytecodeExtension: BytecodeExtension(hostsOnDirectX: spec.HostsOnDirectX)),
            programWordCapacity: spec.ProgramWordCapacity,
            rayQueryEnabled: spec.RayQuery,
            screenSources: spec.ScreenSources,
            screenLights: spec.ScreenLights,
            // Read straight off the frame source (ISdfFrameSource.ScreenSurfaceTransforms, default null) rather than
            // a spec field: this is the ONE place that needs to know the seam exists at all.
            screenSurfaceTransforms: frameSource.ScreenSurfaceTransforms,
            serviceProvider: serviceProvider,
            timingEnabled: spec.Timing,
            viewportCapacity: spec.ViewportCapacity,
            width: spec.Width
        );
        var root = (IRenderNode)producer;

        if (spec.Decorate is { } decorate) {
            root = decorate(producer);
        }

        // Captured BEFORE root is wrapped in SdfWorldRenderRoot below: `root` here is the actual decorated chain
        // (the console overlay wrapping the binding bar wrapping the producer, or a subset/none of that), so this
        // is the true outermost node — the debug-view wrapper adds no capture capability of its own.
        return new SdfWorldRender(Producer: producer, Root: new SdfWorldRenderRoot(producer: producer, inner: root)) {
            CaptureTarget = (root as ICaptureRequestTarget),
        };
    }

    private sealed class SdfWorldRenderRoot(SdfEngineNode producer, IRenderNode inner) : IRenderNode, IDebugViewTarget {
        public int DebugMode {
            get => producer.DebugMode;
            set => producer.DebugMode = value;
        }
        public NodeDescriptor Descriptor => inner.Descriptor;

        public Surface ProduceFrame(in FrameContext context) => inner.ProduceFrame(context: in context);
        public void Dispose() => inner.Dispose();
        public void OnDeviceLost() => inner.OnDeviceLost();
    }
}
