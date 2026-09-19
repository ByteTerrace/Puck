using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One journal entry — the tick and engine tick a mutation applied at, and the mutation itself, which the
/// replay reproduces.</summary>
/// <param name="Tick">The simulation-tick coordinate undo and replay rebase a <c>Cycle</c> epoch against.</param>
/// <param name="EngineTick">The engine-tick coordinate they rebase an <c>Advance</c> epoch against.</param>
/// <param name="Mutation">The applied mutation.</param>
/// <remarks>The two clocks are independent.</remarks>
public readonly record struct WorldJournalEntry(ulong Tick, ulong EngineTick, WorldMutation Mutation);
