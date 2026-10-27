using Puck.Commands;

namespace Puck.World;

/// <summary>An authored input program and its explicitly owned replay boundary. Its recording is derived in memory
/// on the first drive; subsequent drives restore only this boundary and compare with that live recording.</summary>
/// <param name="Name">The scope's identity.</param>
/// <param name="Rows">Complete document rows owned by the scope, including its input mailboxes.</param>
/// <param name="Bodies">Stable body ordinals owned by the scope.</param>
/// <param name="Seats">The principals whose addressed inputs this scope records.</param>
/// <param name="Control">An Int row with request, status, cursor and completed cells, outside the scope.</param>
/// <param name="DurationTicks">The recorded span, including time for the final input to settle.</param>
/// <param name="Inputs">Inputs, in tick order, applied through the ordinary grant-checked mutation door.</param>
public sealed record WorldScopedReplay(string Name, IReadOnlyList<string> Rows, IReadOnlyList<int> Bodies,
    IReadOnlyList<Principal> Seats, string Control, int DurationTicks, IReadOnlyList<WorldScopedReplayInput> Inputs);
/// <summary>One tick-relative, atomic input batch. Values are raw state-cell values, just as at the live door.</summary>
public sealed record WorldScopedReplayInput(int Tick, Principal Principal, IReadOnlyList<WorldScopedReplayCell> Cells);
/// <summary>One addressed numeric input cell.</summary>
public sealed record WorldScopedReplayCell(string Row, string Key, long Value);
