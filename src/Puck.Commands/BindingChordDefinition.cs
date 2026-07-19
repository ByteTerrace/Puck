namespace Puck.Commands;

/// <summary>
/// One chord row of a binding profile: <c>(group, ordered chord) → meaning</c>. The chord is the ORDERED sequence
/// of held modifier ids — <c>["lt", "rt"]</c> and <c>["rt", "lt"]</c> are distinct rows — and the meaning is a
/// discriminated union carried by exactly one of <paramref name="Page"/> (an entry table the chord selects) or
/// <paramref name="Command"/> (a command the chord fires directly). Page switching is not privileged: it is one
/// meaning a chord can carry, declared through the same authoring layers as every other binding.
/// </summary>
/// <remarks>
/// A seat resolves within its ACTIVE group only: the deepest page row whose chord is a press-order prefix of the
/// held modifiers answers the seat's sources, and a command row fires its press edge on the very signal that
/// completes its chord (release when any member releases). The empty chord names the group's RESTING page —
/// exactly one per group, and it must be a page (an empty chord has no completion edge to fire a command with).
/// <see cref="BindingProfile.Compile"/> rejects a row carrying both meanings, neither meaning, or a
/// <c>(group, chord)</c> pair another row already claims.
/// </remarks>
/// <param name="Group">The page-group this row belongs to (e.g. <c>play</c>, <c>editor</c>). A seat's runtime
/// mode is its active group; groups are plain data — the engine never interprets the name.</param>
/// <param name="Chord">The ordered <see cref="BindingModifierDefinition.Id"/>s that must be held, in press order;
/// empty for the group's resting page.</param>
/// <param name="Page">The page meaning: the entry table active while this chord is the deepest held page row, or
/// <see langword="null"/> when the row carries a command meaning.</param>
/// <param name="Command">The command meaning: the command this chord fires directly, or <see langword="null"/>
/// when the row carries a page meaning.</param>
public sealed record BindingChordDefinition(
    string Group,
    IReadOnlyList<string> Chord,
    BindingPageDefinition? Page = null,
    BindingCommandDefinition? Command = null
);

/// <summary>
/// The command meaning of a <see cref="BindingChordDefinition"/>: a direct chord-to-command binding with the full
/// entry semantics a page entry carries — the hold/release shape, an optional constant activation value, and the
/// display metadata an on-screen binding UI presents.
/// </summary>
/// <param name="Command">The name of the command the chord fires.</param>
/// <param name="HoldRelease">Whether the command dispatches on BOTH edges — the press when the chord completes and
/// the release when any member releases (the handler reads the phase to hold-or-free, the page-entry HoldRelease
/// convention). The default dispatches the press edge only; the release still clears the carried held state.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
/// <param name="Value">A constant press value replacing the default active digital, or <see langword="null"/>.
/// Marked <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> for the same reason as
/// <see cref="BindingPageEntryDefinition"/>'s: a CODE-AUTHORING channel, never present in a stored document.</param>
public sealed record BindingCommandDefinition(
    string Command,
    bool HoldRelease = false,
    string? Label = null,
    string? Icon = null,
    [property: System.Text.Json.Serialization.JsonIgnore] CommandValue? Value = null
);
