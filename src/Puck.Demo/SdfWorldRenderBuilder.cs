using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>The assembled SDF world render host a <see cref="SdfWorldRenderBuilder.Build"/> call produced.</summary>
/// <param name="Producer">The SDF engine node itself (the runtime seam for debug captures etc.).</param>
/// <param name="Root">The node to produce frames from: a debug-view wrapper over the producer/decorator.</param>
internal sealed record SdfWorldRender(
    SdfEngineNode Producer,
    IRenderNode Root
);

/// <summary>
/// The ONE assembly path from an <see cref="SdfWorldRenderSpec"/> to a runnable SDF world render host. Every
/// backend-specific choice lives here: kernel bytecode selection (SPIR-V vs DXIL) and decorator availability derive
/// from the spec's resolved host backend — a caller never names a bytecode extension, and Vulkan-service decorators
/// are explicitly skipped (with a notice) on a Direct3D 12 host rather than silently bound.
/// </summary>
internal static class SdfWorldRenderBuilder {
    /// <summary>The kernel bytecode extension for a resolved host backend — the counterpart of the per-child
    /// <c>directX</c> flag (see <see cref="GamingBrickChildNode"/>), kept beside it so the two can never drift.</summary>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public static string BytecodeExtension(bool hostsOnDirectX) => (hostsOnDirectX ? ".dxil" : ".spv");

    /// <summary>Assembles the SDF world render host a spec describes.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The assembled producer and root.</returns>
    public static SdfWorldRender Build(IServiceProvider serviceProvider, SdfWorldRenderSpec spec) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(spec);

        var producer = new SdfEngineNode(
            capturePath: spec.CapturePath,
            children: spec.Children,
            createStorageImage: spec.CreateOutputImage,
            dynamicTransformCapacity: spec.DynamicTransformCapacity,
            frameSource: spec.FrameSource,
            height: spec.Height,
            instanceCapacity: spec.InstanceCapacity,
            kernels: SdfWorldKernels.Load(bytecodeExtension: BytecodeExtension(hostsOnDirectX: spec.HostsOnDirectX), directory: DemoShaders.SdfDirectory),
            programWordCapacity: spec.ProgramWordCapacity,
            screenSources: spec.ScreenSources,
            screenLights: spec.ScreenLights,
            // Read straight off the frame source (ISdfFrameSource.ScreenSurfaceTransforms, default null) rather than
            // a spec field: this is the ONE place that needs to know the seam exists at all.
            screenSurfaceTransforms: spec.FrameSource.ScreenSurfaceTransforms,
            serviceProvider: serviceProvider,
            width: spec.Width
        );
        var root = (IRenderNode)producer;

        if (spec.Decorate is { } decorate) {
            if (spec.HostsOnDirectX) {
                // The current decorators (the binding-bar overlay) bind Vulkan services and SPIR-V bytecode; on a
                // Direct3D 12 host the world renders undecorated rather than binding the wrong backend's modules.
                Console.Error.WriteLine(value: "[sdf-world] the render decorator is Vulkan-only; skipping it on the Direct3D 12 host.");
            } else {
                root = decorate(producer);
            }
        }

        return new SdfWorldRender(Producer: producer, Root: new SdfWorldRenderRoot(producer: producer, inner: root));
    }

    private sealed class SdfWorldRenderRoot(SdfEngineNode producer, IRenderNode inner) : IRenderNode, IDebugViewTarget {
        public NodeDescriptor Descriptor => inner.Descriptor;

        public int DebugMode {
            get => producer.DebugMode;
            set => producer.DebugMode = value;
        }

        public Surface ProduceFrame(in FrameContext context) => inner.ProduceFrame(context: in context);

        public void Dispose() => inner.Dispose();

        public void OnDeviceLost() => inner.OnDeviceLost();
    }
}
