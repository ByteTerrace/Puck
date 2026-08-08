using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// An ORDERED sequence of physical controls that gates or fires a <see cref="BindingPageEntryDefinition"/>, in
/// place of a single <see cref="BindingPageEntryDefinition.Source"/> — the same held-order primitive
/// <see cref="BindingChordDefinition"/> uses for page/command chords, scoped to one binding row instead of a
/// whole group, and widened with a TAPPED variant a simultaneous hold cannot express. Arbitrary length: a
/// one-element sequence is a plain held (or tapped) control; a ten-element sequence is the same primitive at
/// length ten (a tap-sequence easter egg consumes exactly this). Order is significant —
/// <c>["lt", "rt"]</c> and <c>["rt", "lt"]</c> are distinct activators.
/// </summary>
/// <remarks>
/// <para><see cref="BindingActivatorMode.Held"/> mirrors a chord row's page/command meaning: the entry is ACTIVE
/// exactly while every <see cref="Sequence"/> member is held, in press order — gate opens on the signal that
/// completes the order, closes the instant any member releases or the order breaks. A digital member latches
/// held/released at its default press/release hysteresis (0.5 / 0.4, the same constants
/// <see cref="BindingModifierDefinition"/> defaults to); an analog member's live magnitude drives the same
/// hysteresis band.</para>
/// <para><see cref="BindingActivatorMode.Tapped"/> tracks discrete rising edges instead of simultaneous holds: each
/// <see cref="Sequence"/> member must be PRESSED in order (a release between taps is expected, not required — the
/// tracker only watches presses). The entry fires ONCE, on the press that completes the sequence, then resets to
/// await a fresh attempt (never a shortcut through the just-fired sequence's own tail). A mismatch does not discard
/// progress outright: the tracker (<see cref="RowActivatorTracker"/>) runs a proper KMP (Knuth-Morris-Pratt)
/// failure-function walk, falling back to the longest prefix of what's matched so far that is STILL a valid partial
/// match, so a sequence with a repeated PREFIX (<see cref="Sequence"/> permits repeats — see below) never
/// under-matches: <c>[a, a, b]</c> completes on <c>a, a, a, b</c> just as it does on <c>a, a, b</c>, because the
/// third <c>a</c> is still a valid one-tap restart, not wrong input. A partial sequence resets on genuine WRONG
/// INPUT (a press the walk cannot fall back to any prefix of) or on TIMEOUT (<see cref="TimeoutTicks"/> engine
/// ticks since the last accepted step, when set); it does NOT reset on release — a release is simply not a step.
/// Both resets are pure functions of the deterministic signal stream (<see cref="InputSignal.CaptureTick"/>), so a
/// recorded tap sequence replays bit-for-bit.</para>
/// </remarks>
/// <param name="Sequence">The ordered physical control source ids (e.g. <c>gamepad.leftTrigger</c>). At least one
/// element; <see cref="BindingActivatorMode.Held"/> additionally requires every element to be distinct (a
/// simultaneous hold cannot distinguish a repeated control), while <see cref="BindingActivatorMode.Tapped"/>
/// permits repeats (a Konami-style code taps the same control more than once).</param>
/// <param name="Mode">Whether the sequence gates while HELD or fires once on TAP completion. Defaults to
/// <see cref="BindingActivatorMode.Held"/>.</param>
/// <param name="TimeoutTicks">For <see cref="BindingActivatorMode.Tapped"/> only: the maximum engine ticks
/// between two accepted steps before the partial sequence resets, or <see langword="null"/> for no timeout.
/// ENGINE ticks, the same base <see cref="InputSignal.CaptureTick"/> is stamped in (<c>Puck.Hosting.EngineTicks</c>
/// — <c>50400</c> per second) — NOT simulation steps (240 Hz, i.e. 210 engine ticks apart): a "half-second window"
/// is authored as roughly <c>25200</c>, not <c>120</c>. Meaningless (and refused) on a
/// <see cref="BindingActivatorMode.Held"/> activator.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingActivatorDefinition(
    IReadOnlyList<string> Sequence,
    BindingActivatorMode Mode = BindingActivatorMode.Held,
    int? TimeoutTicks = null
);

/// <summary>The two ways a <see cref="BindingActivatorDefinition"/> sequence resolves. See the type's remarks for
/// the exact semantics of each.</summary>
public enum BindingActivatorMode {
    /// <summary>Gates the binding while every sequence member is held, in press order.</summary>
    Held,

    /// <summary>Fires the binding once when the sequence is tapped in order, then resets.</summary>
    Tapped,
}
