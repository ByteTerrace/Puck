namespace Puck.SdfVm;

/// <summary>
/// How much of a frame's mesh draws its raster passes skip (<see cref="SdfFrame.MeshCull"/>). Every level draws the same
/// image; a lower one draws more, so <c>world.cull</c> measures what each saves.
/// </summary>
public enum SdfMeshCullLevel : byte {
    /// <summary>The mesh pass's occlusion test on top of the box cull: each view's mesh pass draws in two phases, what its
    /// previous frame found visible and then what the GPU finds newly visible against the first phase's depth
    /// (<see cref="SdfMeshOcclusion"/>). The far cascade uses the same two-phase entry test in light space and a current
    /// camera receiver mask; its final exit and glass lists never use entry-depth occlusion.</summary>
    Occlusion = 0,
    /// <summary>Camera occlusion with box-only cascade selection, isolating the cost and image of caster culling.</summary>
    Camera = 3,
    /// <summary>The cull by box alone (<see cref="SdfMeshCull"/>): each pass skips the draws outside its view's frustum or
    /// its cascade's light box.</summary>
    Frustum = 1,
    /// <summary>No cull: every pass draws every draw.</summary>
    Off = 2,
}
