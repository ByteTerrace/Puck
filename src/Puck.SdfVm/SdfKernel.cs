namespace Puck.SdfVm;

/// <summary>The SDF engine's compute kernels, in pipeline order: the one table each kernel's file stem
/// (<see cref="SdfKernelSet.StemOf"/>), pipeline, build order and loaded bytecode (<see cref="SdfKernelSet"/>) derive
/// from.</summary>
public enum SdfKernel {
    /// <summary>Conservative swept primary traversal for a residency light camera.</summary>
    LightPrimary,
    /// <summary>Publication of one light camera region into the retained depth bank.</summary>
    LightDepth,
    /// <summary>Residency probe placement and cell partitioning.</summary>
    IndirectClassify,
    /// <summary>Residency transport records and visibility proofs.</summary>
    IndirectTrace,
    /// <summary>Finite lighting of stored transport records into the other radiance and irradiance generation.</summary>
    IndirectShade,
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
    /// <summary>The indirect receiver: each shaded pixel's proof against the residency's cache, its certificate and the
    /// near-field answer that replaces its cache sample, which views reads. Full instruction set.</summary>
    Receiver,
    /// <summary>The indirect receiver with the screen-space and cone comparison methods instead of the near-field sample, whose
    /// pipeline is acquired once a view first selects a comparison method.</summary>
    ReceiverComparison,
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
    /// <summary>One acquired-image reduction per bound screen, shared by direct lighting and indirect face emission.</summary>
    ScreenEmission,
    /// <summary>The carve-union brick baker, dispatched only when the engine keeps a brick pool.</summary>
    BrickBake,
    /// <summary>Full-output reconstruction, whose pipeline is acquired only by reduced or variable views.</summary>
    Resolve,
}
