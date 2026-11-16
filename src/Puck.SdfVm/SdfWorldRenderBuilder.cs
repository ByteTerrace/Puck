using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The ONE assembly path from an <see cref="SdfWorldRenderSpec"/> to the <see cref="SdfWorldResidency"/> that renders it.
/// Every backend-specific choice lives here: kernel bytecode selection (SPIR-V vs DXIL) derives from the spec's resolved
/// host backend, so a caller never names a bytecode extension. What is drawn over a view (post passes, the overlay) is the
/// render graph's business: each view is an <c>sdf.world</c> instance of the graph (<see cref="SdfWorldPasses"/>).
/// </summary>
public static class SdfWorldRenderBuilder {
    /// <summary>Assembles the residency a spec describes.</summary>
    /// <param name="pipelines">The composition's pipeline catalog, forwarded unchanged into the built residency. The factory
    /// reads its kernels through it (<see cref="SdfWorldPipelineCatalog.LoadDeployed"/>).</param>
    /// <param name="spec">The render spec.</param>
    /// <returns>The residency, which the caller holds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> or <paramref name="spec"/> is
    /// <see langword="null"/>.</exception>
    public static SdfWorldResidency Build(SdfWorldPipelineCatalog pipelines, SdfWorldRenderSpec spec) {
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(spec);

        return new SdfWorldResidency(
            dynamicTransformCapacity: spec.DynamicTransformCapacity,
            frameSource: spec.FrameSource,
            height: spec.Height,
            instanceCapacity: spec.InstanceCapacity,
            // The views built over the same cache read this same deployed set, so a boot reads the kernels once.
            kernels: pipelines.LoadDeployed(target: ShaderTargets.Of(hostsOnDirectX: spec.HostsOnDirectX)),
            name: spec.Name,
            pipelines: pipelines,
            programWordCapacity: spec.ProgramWordCapacity,
            screenSources: spec.ScreenSources,
            meshLibrary: spec.MeshLibrary,
            width: spec.Width
        );
    }
}
