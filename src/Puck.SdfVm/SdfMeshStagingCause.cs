namespace Puck.SdfVm;

/// <summary>Why a staging of the mesh region packed a draw record. A staging that does not take the draw journal packs
/// every record under one cause, the first of <see cref="Rebuild"/> through <see cref="Cursor"/> that holds; one that
/// takes it splits its records between <see cref="HandOver"/> and <see cref="Pose"/>. The tables count each cause in
/// <see cref="SdfMeshHistory.ProcessWork"/> (<see cref="SdfMeshHistory.RowsPackedOf"/>).</summary>
public enum SdfMeshStagingCause : byte {
    /// <summary>The frame owes every row because the dresser rebuilt its topology.</summary>
    Rebuild = 0,
    /// <summary>The frame owes every row because a body straddles a portal, or stopped straddling one.</summary>
    Portal = 1,
    /// <summary>The frame owes every row because the publication before it failed.</summary>
    Retry = 2,
    /// <summary>The journal names rows, and the tables refuse it because a mesh is owed, the last staged list was
    /// filtered by landing, its row count differs, or a changed row names a mesh whose words do not stand.</summary>
    Landing = 3,
    /// <summary>The journal cannot be collected: there is none, it is another producer's or list's, its serial
    /// expired, or the tables have staged nothing yet.</summary>
    Cursor = 4,
    /// <summary>The journal is taken, and the record's mesh differs from the one it last staged.</summary>
    HandOver = 5,
    /// <summary>The journal is taken, and the record keeps its mesh: a pose, metadata or a history cut changed.</summary>
    Pose = 6,
}
/// <summary>The first reason that fired for a topology rebuild of the dresser (<see cref="SdfMeshDresser.Dress"/>),
/// counted by <see cref="SdfMeshHistory.TopologyBuildsOf"/>.</summary>
public enum SdfMeshTopologyReason : byte {
    /// <summary>The publication before the frame failed, so it retries from the inputs.</summary>
    Retry = 0,
    /// <summary>Free rows passed one third of the extent.</summary>
    FreeShare = 1,
    /// <summary>A locked frame required canonical row order.</summary>
    Locked = 2,
    /// <summary>The operator requested a canonical start over.</summary>
    Forced = 3,
}
/// <summary>The names <see cref="SdfMeshStagingCause"/>'s members take in work kind names.</summary>
public static class SdfMeshStagingCauses {
    private static readonly string[] Names = ["rebuild", "portal", "retry", "landing", "cursor", "hand-over", "pose"];

    /// <summary>Gets the number of causes.</summary>
    public static int Count => Names.Length;

    /// <summary>Returns a cause's name in a work kind name.</summary>
    /// <param name="cause">The cause.</param>
    /// <returns>The dotted-name segment, such as <c>hand-over</c>.</returns>
    public static string NameOf(SdfMeshStagingCause cause) => Names[((int)cause)];
}
