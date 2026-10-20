using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The settle rebake of a skinned bake group (<see cref="SdfMeshPathBake"/>): a dynamic group whose shapes ride slots
/// besides its own bakes once in the pose its program arrived in, and each vertex then rides its own shape's slot, so a
/// blend seam between shapes that move apart stretches. Once the members' poses in the group's frame have drifted past
/// <see cref="DriftPosition"/> or <see cref="DriftAngle"/> from the pose the mesh was baked at and then held within
/// <see cref="StillPosition"/> and <see cref="StillAngle"/> for <see cref="SettleSeconds"/>, the group is planned again at
/// that pose, rounded to <see cref="PositionSteps"/> and <see cref="OrientationSteps"/> so poses that settle near one
/// another key one geometry and take its mesh from the library, and baked while the skinned mesh keeps drawing. A pose
/// still changing never rebakes, and a residency's bake starts at most <see cref="MaxInFlight"/> settle rebakes at once
/// and <see cref="MaxPerSecond"/> a second.
/// <para>The thresholds are the bake's own constants beside its cell sizes (<see cref="SdfMeshPathBake.FrameTaps"/>):
/// they measure the baked mesh against the field it was sampled from, not anything a world means.</para>
/// </summary>
public static class SdfMeshSettle {
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-settle";
    /// <summary>The distance, in metres, a member's position in the group's frame moves from its baked pose before the
    /// group is a candidate to bake again: past the snapped pose's worst rounding (<see cref="PositionSteps"/>), so a
    /// group baked at its snapped pose is no candidate where it stands.</summary>
    public const float DriftPosition = 0.01f;
    /// <summary>The angle, in radians, a member's orientation in the group's frame turns from its baked pose before the
    /// group is a candidate to bake again: two degrees, past the snapped pose's worst rounding
    /// (<see cref="OrientationSteps"/>).</summary>
    public const float DriftAngle = 0.035f;
    /// <summary>The distance, in metres, a member's pose moves from where the group last saw it change before the group
    /// counts it as still moving.</summary>
    public const float StillPosition = 0.0005f;
    /// <summary>The angle, in radians, a member's pose turns from where the group last saw it change before the group
    /// counts it as still moving.</summary>
    public const float StillAngle = 0.001f;
    /// <summary>The presentation seconds a drifted pose holds still before its group bakes again.</summary>
    public const float SettleSeconds = 0.5f;
    /// <summary>The steps a metre a settled member's position rounds to: 7.8 mm, a worst rounding of 6.8 mm, under
    /// <see cref="DriftPosition"/>.</summary>
    public const float PositionSteps = 128f;
    /// <summary>The steps a unit a settled member's orientation quaternion components round to: a worst rounding of
    /// about 0.9 degrees, under <see cref="DriftAngle"/>.</summary>
    public const float OrientationSteps = 128f;
    /// <summary>The most settle rebakes one residency's bake runs at once.</summary>
    public const int MaxInFlight = 2;
    /// <summary>The most settle rebakes one residency's bake starts in a second of presentation time.</summary>
    public const int MaxPerSecond = 4;

    /// <summary>Counts each settle rebake started: a group planned again at its settled pose.</summary>
    public static readonly WorkKind Queued = new(name: "sdf.mesh-settle.queued", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each settle rebake whose mesh landed and replaced the skinned one, taken from the library or its
    /// store or baked.</summary>
    public static readonly WorkKind Done = new(name: "sdf.mesh-settle.done", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each settle rebake the library or its store answered with a mesh of the same settled geometry,
    /// which baked nothing.</summary>
    public static readonly WorkKind Reused = new(name: "sdf.mesh-settle.reused", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a drifted group waited because its pose was still changing.</summary>
    public static readonly WorkKind Moving = new(name: "sdf.mesh-settle.moving", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each frame a settled, drifted group waited for <see cref="MaxInFlight"/> or
    /// <see cref="MaxPerSecond"/>.</summary>
    public static readonly WorkKind Capped = new(name: "sdf.mesh-settle.capped", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts each settle rebake that meshed no surface, after which the group keeps the mesh it had until its
    /// pose moves again.</summary>
    public static readonly WorkKind Refused = new(name: "sdf.mesh-settle.refused", unit: "groups", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Queued, Done, Reused, Moving, Capped, Refused];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// library exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters one library's bakes count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);
}
