namespace Puck.SdfVm;

/// <summary>The SDF engine's compute kernels, in pipeline order: the one table each kernel's file stem
/// (<see cref="SdfKernelSet.StemOf"/>), pipeline, build order and loaded bytecode (<see cref="SdfKernelSet"/>) derive
/// from.</summary>
public enum SdfKernel {
    /// <summary>The tile prepass, cone-marching each tile's field into its march planes and part bounds.</summary>
    Beam,
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
    /// <summary>The key light's soft shadow, writing the record's K row.</summary>
    Shadow,
    /// <summary>Shading, the full-instruction-set variant.</summary>
    Views,
    /// <summary>Shading with the exotic ops and shapes compiled out.</summary>
    ViewsCore,
    /// <summary>Shading with the heavy warp and noise family compiled out.</summary>
    ViewsFolds,
    /// <summary>The sky pre-pass.</summary>
    Sky,
    /// <summary>The carve-union brick baker, dispatched only when the engine keeps a brick pool.</summary>
    BrickBake,
}
