using Puck.Assets.Documents;

namespace Puck.Commands;

/// <summary>
/// One row of a binding profile: <c>(group, held ∪ chord) → meaning</c>. <paramref name="Held"/> is a SET of members
/// that must be down in any order; <paramref name="Chord"/> is a SEQUENCE that must have been pressed in that order —
/// <c>["lt", "rt"]</c> and <c>["rt", "lt"]</c> are distinct rows. A row may carry both ("hold these, then play
/// this"): the ordered test runs on the press order with the held members removed, so a held member pressed at any
/// point never breaks the sequence. A member is a <see cref="BindingModifierDefinition.Id"/>, or a raw input source
/// id — a source not owned by a declared modifier becomes an implicit modifier with the default thresholds. The
/// meaning is a discriminated union carried by exactly one of <paramref name="Page"/> (an entry table the row
/// selects) or <paramref name="Command"/> (a command the row fires directly).
/// </summary>
/// <remarks>
/// A seat resolves within its active group only: the page row with the most members whose held set is down and
/// whose chord is a press-order prefix of the remaining held order answers the seat's sources; a command row fires
/// its press edge on the very signal that makes the down set exactly its members with the sequence satisfied
/// (release when any member releases). A row with neither list names the group's resting page — exactly one per
/// group, and it must be a page. <see cref="BindingProfile.Compile"/> rejects a row carrying both meanings, neither
/// meaning, a member listed in both lists, two rows with the same identity <c>(group, held, chord)</c>, and two
/// rows over the same member set where either is not chord-only (an ordered path and an unordered set over the same
/// members would answer the same press).
/// </remarks>
/// <param name="Group">The page-group this row belongs to (e.g. <c>play</c>, <c>editor</c>). A seat's runtime
/// mode is its active group; groups are plain data — the engine never interprets the name. A containing world may
/// instead bind the name to a Text state cell with <c>state.&lt;row&gt;[.&lt;key&gt;]</c>.</param>
/// <param name="Chord">The members that must have been pressed in this order, or <see langword="null"/>/empty.</param>
/// <param name="Page">The page meaning, or <see langword="null"/> when the row carries a command meaning.</param>
/// <param name="Command">The command meaning, or <see langword="null"/> when the row carries a page meaning.</param>
/// <param name="Held">The members that must be down, in any order, or <see langword="null"/>/empty.</param>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingChordDefinition(
    DocumentIdentifier Group,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Chord = null,
    BindingPageDefinition? Page = null,
    BindingCommandDefinition? Command = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Held = null
) {
    /// <summary>Gets a value indicating whether this row names the group's resting page (no members).</summary>
    public bool IsResting => ((Chord is not { Count: > 0 }) && (Held is not { Count: > 0 }));
    /// <summary>Gets every member — held first, then the chord in order.</summary>
    public IEnumerable<string> Members => (Held ?? []).Concat(second: (Chord ?? []));
}
/// <summary>
/// The command meaning of a <see cref="BindingChordDefinition"/>: a direct chord-to-destination binding — exactly one
/// of <paramref name="Command"/> or <paramref name="Channel"/>, the same two-kind split
/// <see cref="BindingPageEntryDefinition"/> carries — with the full entry semantics a page entry carries — the
/// hold/release shape, an optional constant activation value or scale, and the display metadata an on-screen binding
/// UI presents, and the input-side hold/toggle lifecycle a channel destination uses.
/// </summary>
/// <param name="Command">The name of the command the chord fires, or <see langword="null"/> when this row carries a
/// <paramref name="Channel"/> destination instead.</param>
/// <param name="Channel">The channel name or role the chord folds into, or <see langword="null"/> when this row
/// carries a <paramref name="Command"/> destination instead.</param>
/// <param name="Scale">The channel destination's scale (raw <c>[-1, 1]</c>); see
/// <see cref="BindingPageEntryDefinition.Scale"/>.</param>
/// <param name="HoldRelease">Whether the command dispatches on both edges — the press when the chord completes and
/// the release when any member releases (the handler reads the phase to hold-or-free, the page-entry HoldRelease
/// convention). The default dispatches the press edge only; the release still clears the carried held state.
/// Meaningful for a <paramref name="Command"/> destination only: a <paramref name="Channel"/> destination always
/// dispatches its release regardless of this flag, because only the channel verb's handler frees the channel — a
/// withheld release would latch it on forever (a Hold channel on the member release, a Toggle channel on the next
/// completion that flips it off).</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
/// <param name="Value">A constant press value replacing the default active digital, or <see langword="null"/>.</param>
/// <param name="Mode">Whether a channel destination follows the chord's live hold or toggles a persistent input
/// latch on each completion. Toggle is refused for a command destination.</param>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingCommandDefinition(
    string? Command = null,
    ChannelRef? Channel = null,
    float? Scale = null,
    bool HoldRelease = false,
    string? Label = null,
    string? Icon = null,
    CommandValue? Value = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] BindingEntryMode Mode = BindingEntryMode.Hold
);
