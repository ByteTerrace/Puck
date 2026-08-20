namespace Puck.World;

/// <summary>One state of a <see cref="WorldSeatModeFamily"/>.</summary>
/// <param name="Name">The state's stable name — the token <c>player.mode &lt;family&gt; &lt;state&gt;</c> takes and the
/// value <c>Client.WorldContextFamilies</c> context rows key on.</param>
/// <param name="Target">The control application this state drives, or <see langword="null"/> for an ordinary state
/// (the seat drives its own body normally). <c>"camera"</c> is the only admitted value: entering the state diverts
/// the seat's own body intent to <see cref="Puck.World.Protocol.IntentSource.Idle"/> (the existing
/// <c>player.control</c> idle contract) and drives the world-authored <c>views.flyRig</c> from the seat's channels
/// instead.</param>
public sealed record WorldSeatModeState(string Name, string? Target = null) {
    /// <summary>The only admitted <see cref="Target"/> value — the fly control application.</summary>
    public const string CameraTarget = "camera";
}
/// <summary>An AUTHORED per-seat mode family — a document-declared name plus its admitted states.
/// <c>player.mode &lt;family&gt; &lt;state&gt; [seat]</c> flips a seat's published state within the family;
/// <c>Puck.World.Client.WorldContextFamilies</c> context rows may then map a (family, state) pair to a binding group
/// exactly as a built-in family does.</summary>
/// <param name="Name">The family's stable name. Must not collide with a built-in family name (<c>roster</c>,
/// <c>engagement</c>, <c>layout</c>) or the reserved <c>state:</c> prefix.</param>
/// <param name="States">The family's non-empty, uniquely named admitted states.</param>
/// <param name="DefaultState">The state a seat publishes before any <c>player.mode</c> flip — one of
/// <see cref="States"/>.</param>
public sealed record WorldSeatModeFamily(string Name, IReadOnlyList<WorldSeatModeState> States, string DefaultState) {
    private readonly IReadOnlyList<WorldSeatModeState> m_states = (States ?? []);

    /// <summary>Gets the family's admitted states. The absence-coalesce lives in the accessor for the same reason
    /// <see cref="WorldMotionModel.Grounded.Response"/>'s does.</summary>
    public IReadOnlyList<WorldSeatModeState> States {
        get => m_states;
        init => m_states = (value ?? []);
    }
}
