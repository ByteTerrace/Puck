namespace Puck.Physics;

/// <summary>The implemented gravity evaluation strategies.</summary>
public enum GravitySolverKind : byte {
    /// <summary>All source-target interactions, serving as the exact deterministic oracle.</summary>
    Pairwise,

    /// <summary>An adaptive octree whose accepted distant cells contribute their total mass at their center of mass.</summary>
    FastMonopole,

    /// <summary>An adaptive dual-tree fast multipole solve with first-order Cartesian local expansions.</summary>
    AdaptiveFmm,
}
