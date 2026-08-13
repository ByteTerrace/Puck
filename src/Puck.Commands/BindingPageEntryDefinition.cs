using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// One entry of a binding page: a physical source mapped to a DESTINATION while that page is active — exactly one of
/// <paramref name="Command"/> (a dispatched verb with a handler) or <paramref name="Channel"/> (a folded intent-vector
/// row). The two destinations are deliberately NOT unified into one: a command is a dispatched verb and a channel is
/// a folded value, so making a roster confirm a folded value would put roster lifecycle inside the intent fold. It carries the
/// full runtime expressiveness of a compiled <see cref="CommandBinding"/> — a constant activation value a digital
/// source drives an analog channel with (<paramref name="Value"/>, or <paramref name="Scale"/> for a channel
/// destination) — plus the display metadata an on-screen binding UI presents (both opaque strings the engine never
/// interprets).
/// </summary>
/// <param name="Source">The provider-neutral input source id (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>),
/// optionally naming a two-dimensional control's axis component (<c>gamepad.leftStick.x</c> — see
/// <see cref="BindingSourceComponent"/>), or <see langword="null"/> when <paramref name="Activator"/> carries the
/// row's trigger instead. Exactly one of <paramref name="Source"/> or <paramref name="Activator"/> must be set;
/// <see cref="BindingProfile.Compile"/> is the structural gate.</param>
/// <param name="Command">The name of the command this source activates while the page is active, or
/// <see langword="null"/> when this row carries a <paramref name="Channel"/> destination instead. Exactly one of the
/// two must be set; <see cref="BindingProfile.Compile"/> is the structural gate.</param>
/// <param name="Channel">The channel name or role this source folds into, or <see langword="null"/> when this row
/// carries a <paramref name="Command"/> destination instead.</param>
/// <param name="Scale">The channel destination's scale (raw <c>[-1, 1]</c>), applied to a digital source's constant
/// activation — the mechanism that lets two opposing rows on one channel (e.g. W/S on "forward") replace a pair of
/// per-direction commands. Defaults to <c>+1</c>; meaningless (and refused at the vocabulary gate) on a binary
/// channel destination other than the default.</param>
/// <param name="ActivateOn">The phase the binding fires on, or <see langword="null"/> for the default (press/continuous, not release).
/// Meaningless (and refused) beside <paramref name="Activator"/> — the activator's own transition IS the entry's edge.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
/// <param name="Value">A constant activation value a <paramref name="Command"/> destination's digital source sends
/// instead of its own (a function key driving a fixed one-dimensional axis), or <see langword="null"/> to pass the
/// source's value through.</param>
/// <param name="Activator">An ORDERED sequence of physical controls that gates or fires this entry in place of a
/// single <paramref name="Source"/> (see <see cref="BindingActivatorDefinition"/>), or <see langword="null"/> for
/// an ordinary single-source entry. Omitted from a saved document when <see langword="null"/>, so a document
/// authored before this member existed round-trips byte-identical.</param>
/// <param name="Mode">Whether a HELD digital destination reads the physical control's live hold (the default,
/// byte-identical with every document authored before this member existed) or an input-side TOGGLE latch (see
/// <see cref="BindingEntryMode"/>). Only meaningful on a <paramref name="Channel"/> destination;
/// <see cref="BindingProfile.Compile"/> refuses <see cref="BindingEntryMode.Toggle"/> on a
/// <paramref name="Command"/> destination. Omitted from a saved document at its default value, so a document
/// authored before this member existed round-trips byte-identical.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingPageEntryDefinition(
    string? Source,
    string? Command = null,
    ChannelRef? Channel = null,
    float? Scale = null,
    CommandPhase? ActivateOn = null,
    string? Label = null,
    string? Icon = null,
    CommandValue? Value = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] BindingActivatorDefinition? Activator = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] BindingEntryMode Mode = BindingEntryMode.Hold
) {
    /// <summary>Gets a stable label identifying this entry's trigger for a diagnostic or a binding-bar chip: its
    /// <see cref="Source"/>, else its activator sequence rendered as <c>activator[a,b,…]</c>, else <c>(unset)</c> when
    /// the structural gate has not yet refused a trigger-less entry. The one label an entry renders by. Null-tolerant
    /// on the sequence (a deserialized document can carry an activator with no <c>sequence</c>): the label labels a
    /// refusal, so it must never itself throw on the malformed shape it is describing.</summary>
    internal string TriggerLabel => (Source ?? ((Activator is { } activator)
        ? $"activator[{string.Join(
        separator: ',',
        values: (activator.Sequence ?? [])
    )}]"
        : "(unset)"));
}
