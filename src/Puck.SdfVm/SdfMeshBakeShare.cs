using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// One bake of a geometry under a bake identity that the groups of every residency holding the library wait on, rather
/// than each baking it again (<see cref="SdfMeshLibrary.Share"/>): the library names the bake running each geometry, a
/// group of any residency that plans the same geometry while it runs waits on it, and each waiter takes the mesh from the
/// library when it lands.
/// <para>Invariant: a waiter is never left waiting on a bake that will not finish. The library's gate is the one place a
/// share is joined, left and resolved, so a share resolved (its leader done or retired) is gone from the registry before
/// any later group can join it, and every waiter joined before is handed back. The leader's group resolves its share
/// through its own <c>Done</c> and <c>Retired</c> setters, the one door every path that finishes or drops a group passes;
/// a waiter whose leader's bake no frame drives deserts it (<c>SdfMeshPathBake.DesertStalled</c>).</para>
/// <para>Which group bakes a geometry never changes what it meshes: a mesh is a function of its geometry and the bake's
/// identity, which the share's key holds, so the share stays outside the bake's code identity
/// (<see cref="SdfMeshBakeClosure"/>).</para>
/// </summary>
public sealed class SdfMeshBakeShare {
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-shares";

    /// <summary>Counts each group that waited on another group's bake of its geometry through the library instead of
    /// baking it (a group of another residency, or one its own residency's restart index did not hold).</summary>
    public static readonly WorkKind Joined = new(name: "sdf.mesh-shares.joined", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each waiting group that took the mesh its leader landed.</summary>
    public static readonly WorkKind Landed = new(name: "sdf.mesh-shares.landed", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the samples the leaders of landed waiters dispatched for their geometry: what each waiter would
    /// have sampled baking it again.</summary>
    public static readonly WorkKind SamplesSaved = new(name: "sdf.mesh-shares.samples-saved", unit: "samples", workClass: WorkClass.Pacing);
    /// <summary>Counts each waiting group whose leader finished with no mesh in the library (retired, failed, or its mesh
    /// trimmed from the library before the waiter took it), which then started its geometry again.</summary>
    public static readonly WorkKind Abandoned = new(name: "sdf.mesh-shares.abandoned", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each waiting group that deserted a leader whose residency's background work was never asked for a
    /// lattice while the waiter's was (a residency no frame renders), and started its geometry itself.</summary>
    public static readonly WorkKind Stalled = new(name: "sdf.mesh-shares.stalled", unit: "groups", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Joined, Landed, SamplesSaved, Abandoned, Stalled];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// library exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters one library's bakes count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);

    internal SdfMeshBakeShare(UInt128 identity, SdfMeshGeometry geometry, SdfMeshPathBake bake) {
        Identity = identity;
        Geometry = geometry;
        Bake = bake;
    }

    // The bake identity and geometry the share bakes, the bake whose group leads it, and the samples the leader
    // dispatched for the geometry, written when it resolves.
    internal UInt128 Identity { get; }
    internal SdfMeshGeometry Geometry { get; }
    internal SdfMeshPathBake Bake { get; }
    internal long Samples { get; set; }

    // The groups waiting on the share, and whether it has resolved. Under the library's gate.
    internal List<Waiter> Waiters { get; } = [];
    internal bool Resolved { get; set; }

    // How many groups wait, so a leader with none (most) reads no gate for its priority.
    private int m_waiting;

    internal bool HasWaiters => (Volatile.Read(location: ref m_waiting) > 0);

    internal void Recount() => Volatile.Write(location: ref m_waiting, value: Waiters.Count);

    /// <summary>One group waiting on a share: its bake, the group, and the priority its bake last measured it at, which
    /// the leader's bake ranks its group by when that is higher (<c>SdfMeshPathBake.Prioritize</c>).</summary>
    /// <param name="bake">The waiting group's bake.</param>
    /// <param name="group">The waiting group.</param>
    internal sealed class Waiter(SdfMeshPathBake bake, object group) {
        public SdfMeshPathBake Bake { get; } = bake;
        public object Group { get; } = group;

        private float m_priority;

        public float Priority { get => Volatile.Read(location: ref m_priority); set => Volatile.Write(location: ref m_priority, value: value); }
    }
}
