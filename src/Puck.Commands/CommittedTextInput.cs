namespace Puck.Commands;

/// <summary>Committed characters in the same capture order as bound input, never a submitted command line.</summary>
/// <param name="Principal">Identity stamped by the input router's slot resolver.</param>
/// <param name="Slot">Logical input slot.</param>
/// <param name="Text">Complete keyboard, paste, or IME commit.</param>
/// <param name="Tick">Host snapshot tick.</param>
/// <param name="CaptureTick">Original capture-clock coordinate.</param>
/// <param name="Activation">Capture identity shared with this commit's scalar events.</param>
/// <param name="WindowStart">Inclusive host capture interval start.</param>
/// <param name="WindowEnd">Exclusive host capture interval end.</param>
/// <param name="SimulationOffset">Optional exact offset authored by a scripted signal.</param>
public readonly record struct CommittedTextInput(Principal Principal, int Slot, string Text, ulong Tick,
    ulong CaptureTick, ulong Activation, ulong WindowStart, ulong WindowEnd, Puck.Maths.FixedQ4816? SimulationOffset = null);
