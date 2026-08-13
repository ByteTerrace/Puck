namespace Puck.Commands;

/// <summary>
/// One synthesized chord-command edge: a command a chord row fired that no single source signal expresses — the
/// press when the chord completed, or the release when a member released. The <see cref="InputRouter"/> folds
/// these into the slot's lane with the edge's OWN phase and value, because the physical signal that caused the
/// transition (an analog trigger crossing a hysteresis threshold mid-<see cref="CommandPhase.Active"/>) does not
/// carry the phase the command's handler must see.
/// </summary>
/// <param name="Command">The name of the command the edge drives.</param>
/// <param name="Phase">The edge: <see cref="CommandPhase.Started"/> on chord completion, <see cref="CommandPhase.Completed"/> on chord break.</param>
/// <param name="Value">The value the edge carries (the row's press value, or its inactive twin on release).</param>
/// <param name="Dispatch">Whether the edge's handler fires (a press always dispatches; a release dispatches only
/// for a <see cref="BindingCommandDefinition.HoldRelease"/> row — either way the release clears the carried held state).</param>
/// <param name="Momentary">Whether a <see cref="CommandPhase.Started"/> edge should carry no held state forward
/// (<see langword="false"/>, the default, is every existing chord-command/Held-activator row: it marks the router's
/// carried-held table exactly like a physical hold, re-asserting every subsequent tick until a real
/// <see cref="CommandPhase.Completed"/> edge arrives). <see langword="true"/> is a
/// <see cref="BindingActivatorMode.Tapped"/> completion's press: its own release is already scheduled one tick
/// later (see <see cref="IChordEdgeSource.DrainScheduledEdges"/>), so marking it held too would make the tick
/// carrying that scheduled release also carry a stale, non-dispatching re-assertion of the press — harmless to a
/// dispatch-gated reader, but not the clean single-entry pulse a tap is supposed to produce. Ignored on a
/// <see cref="CommandPhase.Completed"/> edge (a release never marks anything held regardless).</param>
public readonly record struct BindingChordEdge(
    string Command,
    CommandPhase Phase,
    CommandValue Value,
    bool Dispatch,
    bool Momentary = false
);

/// <summary>
/// The seam a chord-aware <see cref="IInputBindings"/> hands its synthesized chord-command edges to the
/// <see cref="InputRouter"/> through. After each <see cref="IInputBindings.Resolve(int, in InputSignal)"/> the
/// router drains the slot's pending edges and folds them into the same tick's lane — so a chord-fired command is
/// <see cref="CommandSnapshot"/>-visible, held-tracked, and replayed exactly like a source-bound one.
/// </summary>
public interface IChordEdgeSource {
    /// <summary>Drains the chord-command edges the most recent signal resolve synthesized for a slot.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <returns>The pending edges, in transition order. The span aliases an internal per-slot buffer that the next
    /// resolve for the slot reuses — consume it before resolving another signal.</returns>
    ReadOnlySpan<BindingChordEdge> DrainChordEdges(int slot);

    /// <summary>Drains every edge a PRIOR tick's signal processing scheduled to fire on the tick AFTER it — a
    /// <see cref="BindingActivatorMode.Tapped"/> row activator's completion is a momentary PULSE (the press fires
    /// immediately; its release is deferred one tick rather than collapsing into the same
    /// <see cref="CommandSnapshot"/>, which a downstream reader that only samples state between ticks would never
    /// observe — a same-tick press+release is indistinguishable from never having pressed at all).</summary>
    /// <returns>Every (slot, edge) pair now due, in scheduling order. Empty when nothing is pending. The list may
    /// alias retained internal storage; consume it before resolving another signal or resetting the bindings.</returns>
    /// <remarks>Called exactly once per tick, by <see cref="InputRouter"/>, before that tick folds its own due
    /// signals — so anything scheduled during this tick's signal processing is, by construction of that call
    /// order, deferred to the next tick's call rather than drained again immediately. No clock or engine-tick
    /// arithmetic is involved; the ordering IS the one-tick delay.</remarks>
    IReadOnlyList<(int Slot, BindingChordEdge Edge)> DrainScheduledEdges() {
        return [];
    }
}
