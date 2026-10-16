namespace Puck.SdfVm;

/// <summary>The SDF engine's compute kernels, in pipeline order: the one table each kernel's file stem
/// (<see cref="SdfKernelSet.StemOf"/>), pipeline, build order and loaded bytecode (<see cref="SdfKernelSet"/>) derive
/// from.</summary>
public enum SdfKernel {
    /// <summary>The tile prepass, cone-marching each tile's field into its march planes and part bounds.</summary>
    Beam,
    /// <summary>The certified per-tile live-segment mask.</summary>
    Tape,
    /// <summary>The per-tile instance mask.</summary>
    InstanceCull,
    /// <summary>The reduction of the surviving tiles to the hit passes' indirect arguments and dispatch box.</summary>
    CullArgs,
    /// <summary>Primary traversal, writing the visibility record's V, C and L rows.</summary>
    Primary,
    /// <summary>The geometric normal and curvature, writing the record's N and S rows.</summary>
    Surface,
    /// <summary>Ambient occlusion, updating the record's S row.</summary>
    Ambient,
    /// <summary>The stable and incoming soft shadows, writing the record's K row and, at a nonzero fade capacity, the
    /// incoming visibility image; one kernel serves every capacity.</summary>
    Shadow,
    /// <summary>Shading, the full-instruction-set variant.</summary>
    Views,
    /// <summary>Shading with the exotic ops and shapes compiled out.</summary>
    ViewsCore,
    /// <summary>Shading with the heavy warp and noise family compiled out.</summary>
    ViewsFolds,
    /// <summary>The sky's field runs, evaluated where the lit image's coverage is below one.</summary>
    Sky,
    /// <summary>The composite: the sky's runs, the lit image over them by its coverage, the fog and the bounded media.</summary>
    Composite,
    /// <summary>The sky's environment map, the gradient in every texel's direction, dispatched by the residency's
    /// <c>sdf.environment</c> graph producer when lighting-visible irradiance crosses one display code.</summary>
    SkyEnvironment,
    /// <summary>The environment map's reduction to its spherical-harmonic coefficients, dispatched after the map.</summary>
    SkyEnvironmentReduce,
    /// <summary>One acquired-image reduction per bound screen, which direct lighting reads.</summary>
    ScreenEmission,
    /// <summary>The carve-union brick baker, dispatched only when the engine keeps a brick pool.</summary>
    BrickBake,
    /// <summary>The baked-mesh path's surface pass in place of primary, surface, ambient and shadow: the visibility record
    /// from the mesh target, screen-space occlusion and the shadow cascades, with no field query.</summary>
    MeshSurface,
    /// <summary>The mesh bake's instance mask, selecting one bake group's instances.</summary>
    MeshBakeMask,
    /// <summary>The mesh bake's field sampler over one group's lattice. Full instruction set.</summary>
    MeshBake,
    /// <summary>The baked-mesh path's shading: the views pass with its field reads compiled out, so it carries no
    /// interpreter.</summary>
    ViewsMesh,
    /// <summary>Full-output reconstruction, whose pipeline is acquired only by reduced or variable views.</summary>
    Resolve,
}
