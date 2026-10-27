using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A scoped tape's continuation inside an authority checkpoint. The authored program remains in the
/// definition; this holds its derived start, accepted inputs and live tick hashes, never a committed ledger.</summary>
public sealed record WorldScopedReplayCheckpoint(string Name, ArenaScopeSnapshot? Start,
    IReadOnlyList<WorldPopulationEntryCheckpoint> Bodies, ulong StartHash,
    IReadOnlyList<(int Tick, WorldMutation Mutation)> Inputs, IReadOnlyList<ulong> Hashes,
    int Cursor, int InputCursor, bool Recording, bool Active, bool Diverged,
    IReadOnlyList<(int Tick, WorldCommand Command)> Commands,
    IReadOnlyList<(int Tick, IntentSubmission Intent)> Intents);
