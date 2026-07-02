namespace Puck.Commands;

/// <summary>
/// A pre-resolved command queued for the deterministic input path: a command that is already bound to its interned
/// id and value (a console / STDIN line, a network peer, or an AI driver), as opposed to a physical
/// <see cref="InputSignal"/> that still needs a binding-table lookup. The <see cref="InputRouter"/> folds it into a
/// per-tick <see cref="CommandSnapshot"/> alongside captured signals — so once injected, it is recorded and replayed
/// for free.
/// </summary>
/// <param name="CommandId">The interned command id (<see cref="CommandRegistry.TryGetId"/>) the value drives.</param>
/// <param name="Value">The command's value for the tick it folds into.</param>
/// <param name="Phase">
/// The edge the injection represents. <see cref="CommandPhase.Started"/> dispatches as a one-shot press, the natural
/// shape for a console impulse; an injection is never held across ticks (it appears only in the tick it folds into).
/// </param>
/// <param name="Slot">The logical player slot the command drives (a console command targets the local slot, <c>0</c>).</param>
/// <param name="CaptureTick">
/// The capture time, in engine ticks, that attributes the command to a fixed-step tick. <c>0</c> lets the router
/// stamp it from the shared capture clock when it arrives (the live path); a producer that already knows the tick
/// (a deterministic script or a replay-grade harness) sets it explicitly.
/// </param>
public readonly record struct CommandInjection(
    ushort CommandId,
    CommandValue Value,
    CommandPhase Phase,
    int Slot = 0,
    ulong CaptureTick = 0UL
);
