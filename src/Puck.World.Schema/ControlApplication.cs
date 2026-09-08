namespace Puck.World.Protocol;

/// <summary>
/// One control application — the <c>(target, kit)</c> pair a principal's resolved intent flows through. A principal
/// holds a SET of these, and the set is the whole of what engagement is: an unengaged participant holds exactly its
/// own-body application (<see cref="OwnBody"/>), so the avatar integrates its own intent; composing a target
/// application onto a machine-pad screen or another body adds a member; capturing (the old <c>capture:true</c>)
/// REMOVES the own-body member, which is what idles the avatar; mirroring keeps both members present. There is no
/// separate capture flag and no separate route row — membership is the state, so a latch and a route can no longer
/// disagree.
/// </summary>
/// <param name="Target">The subject the application delivers to — a <see cref="GrantSubjectKind.Screen"/> (a booted
/// machine's pad) or a <see cref="GrantSubjectKind.Body"/> (its own body, or another body under possession).</param>
/// <param name="Kit">The <see cref="WorldKit"/> name whose <c>pad</c> map gives the delivered channels their meaning
/// at the target, or <see langword="null"/> for passthrough — every reached ordinal arrives unchanged, which is what
/// a body target always wants (the destination body's own kit already assigns meaning) and what a screen naming no
/// kit falls back to (the engine's two-movement-role default pad).</param>
/// <param name="Reach">The channel ordinals this application delivers at all. A masked-out ordinal still drives the
/// source body through its own-body application (when that member is present) but never reaches this target.</param>
public readonly record struct ControlApplication(GrantSubject Target, string? Kit, ChannelReachMask Reach) {
    /// <summary>Creates the own-body application — passthrough over every ordinal onto the participant's own body.
    /// Its presence in the set is exactly "the avatar drives itself"; its absence is exactly "captured".</summary>
    /// <param name="bodyIndex">The 0-based entity index of the participant's own body.</param>
    public static ControlApplication OwnBody(int bodyIndex) => new(
        Kit: null,
        Reach: ChannelReachMask.All,
        Target: GrantSubject.Body(index: bodyIndex)
    );
    /// <summary>Describes the application for a console echo — <c>&lt;target&gt;[/&lt;kit&gt;](mask=0x…)</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => $"{Target.Describe()}{((Kit is { Length: > 0 } kit)
        ? $"/{kit}"
        : string.Empty)}(mask=0x{Reach.Bits:x4})";
}
