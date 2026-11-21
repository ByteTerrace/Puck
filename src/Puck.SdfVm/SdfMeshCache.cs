using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// What the bakes holding one library (<see cref="SdfMeshLibrary.Policy"/>) do about a rung of a group's tier ladder
/// (<see cref="SdfMeshTier"/>: every tier between <see cref="SdfMeshBakeGroup.FinestTier"/> and
/// <see cref="SdfMeshBakeGroup.CoarsestTier"/> is its own store entry) that neither the library nor its store holds.
/// </summary>
public enum SdfMeshCachePolicy : byte {
    /// <summary>Bakes the rung the demand asks, and no other: what an author's own boot does.</summary>
    Demand,
    /// <summary>Bakes the rung the demand asks and then every other rung of each group's ladder, into the store alone:
    /// what the producer of a whole cache does, so a later boot that draws only stored rungs
    /// (<see cref="Stored"/>) finds whichever one its own demand, window size or memory budget asks.</summary>
    Ladder,
    /// <summary>Draws stored rungs alone: a rung the store lacks is never baked while it holds another rung of the same
    /// shape, which draws in its place (a group arriving takes the nearest stored rung, coarser first, and a group
    /// already drawing keeps what it draws), and a load the account has no room for is tried again rather than baked.
    /// Only a shape the store holds no rung of bakes, since nothing else can draw it. What a build that ships its cache
    /// runs.</summary>
    Stored,
}
/// <summary>
/// The counts of what the bakes holding one library asked of its cache beyond a hit
/// (<see cref="SdfMeshCachePolicy"/>): the load-independent reading of whether a shipped cache covers what a boot
/// demands. A boot of a whole cache under <see cref="SdfMeshCachePolicy.Stored"/> counts none of the first three.
/// </summary>
public static class SdfMeshCache {
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-cache";

    /// <summary>Counts each lattice a bake handed the GPU to sample: zero for a boot that bakes nothing.</summary>
    public static readonly WorkKind Dispatches = new(name: "sdf.mesh-cache.dispatches", unit: "lattices", workClass: WorkClass.Pacing);
    /// <summary>Counts each group that began baking its geometry because neither the library nor its store held it:
    /// under <see cref="SdfMeshCachePolicy.Stored"/>, a shape the store holds no rung of.</summary>
    public static readonly WorkKind GroupsBaked = new(name: "sdf.mesh-cache.groups-baked", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each rung a <see cref="SdfMeshCachePolicy.Stored"/> bake asked that the store lacked, which another
    /// rung of the shape drew in place of.</summary>
    public static readonly WorkKind RungsMissed = new(name: "sdf.mesh-cache.rungs-missed", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each load a <see cref="SdfMeshCachePolicy.Stored"/> bake tried again because the account had no
    /// room for its workspace.</summary>
    public static readonly WorkKind LoadsRetried = new(name: "sdf.mesh-cache.loads-retried", unit: "loads", workClass: WorkClass.Pacing);
    /// <summary>Counts each rung a <see cref="SdfMeshCachePolicy.Ladder"/> bake baked beyond the one its demand asked.</summary>
    public static readonly WorkKind RungsFilled = new(name: "sdf.mesh-cache.rungs-filled", unit: "groups", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Dispatches, GroupsBaked, RungsMissed, LoadsRetried, RungsFilled];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// library exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters one library's bakes count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
