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
public readonly record struct BindingChordEdge(
    string Command,
    CommandPhase Phase,
    CommandValue Value,
    bool Dispatch
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
}
