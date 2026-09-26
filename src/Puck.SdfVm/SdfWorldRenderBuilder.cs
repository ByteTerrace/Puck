using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The ONE assembly path from an <see cref="SdfWorldRenderSpec"/> to the <see cref="SdfEngineNode"/> that renders it.
/// Every backend-specific choice lives here: kernel bytecode selection (SPIR-V vs DXIL) derives from the spec's resolved
/// host backend, so a caller never names a bytecode extension. What is drawn over the engine's output (post passes, the
/// overlay) is the render graph's business: the node is the <c>sdf.world</c> producer a graph instance reads.
/// </summary>
public static class SdfWorldRenderBuilder {
    /// <summary>Assembles the engine node a spec describes.</summary>
    /// <param name="pipelines">The composition's pipeline cache, forwarded unchanged into the built
    /// <see cref="SdfEngineNode"/>. The factory reads the node's kernels through it
    /// (<see cref="SdfWorldPipelineCache.LoadDeployed"/>).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The engine node, which the caller owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> or <paramref name="spec"/> is
    /// <see langword="null"/>.</exception>
    public static SdfEngineNode Build(SdfWorldPipelineCache pipelines, SdfWorldRenderSpec spec) {
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

        return producer;
    }
    /// <summary>The kernel bytecode extension for a resolved host backend — the counterpart of the per-child
    /// <c>directX</c> flag (the GamingBrick child node), kept beside it so the two can never drift.</summary>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    public static string BytecodeExtension(bool hostsOnDirectX) => ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX);
}
