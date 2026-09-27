using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The ONE assembly path from an <see cref="SdfWorldRenderSpec"/> to the <see cref="SdfWorldResidency"/> that renders it.
/// Every backend-specific choice lives here: kernel bytecode selection (SPIR-V vs DXIL) derives from the spec's resolved
/// host backend, so a caller never names a bytecode extension. What is drawn over a view (post passes, the overlay) is the
/// render graph's business: each view is an <c>sdf.world</c> instance of the graph (<see cref="SdfWorldPasses"/>).
/// </summary>
public static class SdfWorldRenderBuilder {
    /// <summary>Assembles the residency a spec describes.</summary>
    /// <param name="pipelines">The composition's pipeline cache, forwarded unchanged into the built residency. The factory
    /// reads its kernels through it (<see cref="SdfWorldPipelineCache.LoadDeployed"/>).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The residency, which the caller holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> or <paramref name="spec"/> is
    /// <see langword="null"/>.</exception>
    public static SdfWorldResidency Build(SdfWorldPipelineCache pipelines, SdfWorldRenderSpec spec) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(spec);

        // The frame-source decorator seam (SdfWorldRenderSpec.DecorateFrameSource): a host wraps the scene's frame
        // source here (e.g. the overworld's diegetic-UI director, which emits its own SDF geometry into the program)
        // before the residency is built. Identity when the spec supplies none — most callers (the document-driven world
        // path) never set it.
        var frameSource = ((spec.DecorateFrameSource is { } decorateFrameSource)
            ? decorateFrameSource(spec.FrameSource)
            : spec.FrameSource
        );

        return new SdfWorldResidency(
            brickPoolVoxelCapacity: spec.BrickPoolVoxelCapacity,
            dynamicTransformCapacity: spec.DynamicTransformCapacity,
            frameSource: frameSource,
            height: spec.Height,
            instanceCapacity: spec.InstanceCapacity,
            // The views built over the same cache read this same deployed set, so a boot reads the kernels once.
            kernels: pipelines.LoadDeployed(bytecodeExtension: BytecodeExtension(hostsOnDirectX: spec.HostsOnDirectX)),
            name: spec.Name,
            pipelines: pipelines,
            programWordCapacity: spec.ProgramWordCapacity,
            screenSources: spec.ScreenSources,
            width: spec.Width
        );
    }
    /// <summary>Returns the kernel bytecode extension for a resolved host backend.</summary>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    /// <returns>The extension of the backend's kernel bytecode files, with its leading dot.</returns>
    public static string BytecodeExtension(bool hostsOnDirectX) => ShaderBytecode.FileExtension(hostsOnDirectX: hostsOnDirectX);
}
