namespace Puck.Commands;

/// <summary>
/// A pre-resolved command queued for the deterministic input path: a command that is already bound to its interned
/// id and value (a console / STDIN line or a compiler-produced interface activation), as opposed to a physical
/// <see cref="InputSignal"/> that still needs a binding-table lookup. The <see cref="InputRouter"/> folds it into a
/// per-tick <see cref="CommandSnapshot"/> alongside captured signals — so once injected, it is tick-aligned and
/// applied like any other deterministic input; a world tape records the server's input stream that produced it,
/// not the snapshot itself.
/// </summary>
/// <remarks>
/// INTERNAL, and the <see cref="Principal"/>/<see cref="Slot"/> lanes are why. A public injection carrying a
/// caller-chosen identity would be an ingress door that stamps whatever it is handed. The public surface is
/// <see cref="CommandInjectionSink"/> instead: it binds both lanes AT CONSTRUCTION and fills them here, so the
/// identity is a property of the door rather than of the message.
/// </remarks>
/// <param name="CommandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>) the value drives.</param>
/// <param name="Value">The command's value for the tick it folds into.</param>
/// <param name="Phase">
/// The edge the injection represents. <see cref="CommandPhase.Started"/> dispatches as a one-shot press, the natural
/// shape for a console impulse; an injection is never held across ticks (it appears only in the tick it folds into).
/// </param>
/// <param name="Principal">The identity the injecting door is bound to.</param>
/// <param name="Slot">The logical player slot the command drives (a console command targets the local slot, <c>0</c>).</param>
/// <param name="CaptureTick">
/// The capture time, in engine ticks, that attributes the command to a fixed-step tick. <c>0</c> lets the router
/// stamp it from the shared capture clock when it arrives (the live path); a producer that already knows the tick
/// (a deterministic script or a replay-grade harness) sets it explicitly.
/// </param>
/// <param name="Text">The original simulation-command line, when the injection came from console text. Preserved in
/// the snapshot so argument-bearing verbs execute at tick time.</param>
/// <param name="Source">The logical authored source for a compiler-minted presentation activation, or
/// <see langword="null"/> for console/peer injections without a binding source.</param>
internal readonly record struct CommandInjection(
    ushort CommandId,
    CommandValue Value,
    CommandPhase Phase,
    CommandPrincipal Principal,
    int Slot = 0,
    ulong CaptureTick = 0UL,
    string? Text = null,
    string? Source = null
) {
    /// <summary>Gets a value indicating whether applying this local live injection releases <see cref="TextCommandSource"/>'s deferred-mutation
    /// drain barrier. This is process-local coordination, not deterministic snapshot identity.</summary>
    internal bool CompletesTextSubmission { get; init; }

    internal TextSubmissionBarrier? SubmissionBarrier { get; init; }

    internal bool DispatchWhenMapInactive { get; init; }
}
