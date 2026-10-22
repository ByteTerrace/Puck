namespace Puck.World.Server;

/// <summary>What one publication of the arena installs: the document it leaves installed, and which of the consumers
/// that keep their own copy of a state value it owes an update.</summary>
/// <param name="Definition">The installed document carrying the arena's values.</param>
/// <param name="Moved">Whether any row differs from the installed document. When <see langword="false"/>,
/// <paramref name="Definition"/> is the installed document itself.</param>
/// <param name="DriveGate">Whether a row that gates a drive moved.</param>
/// <param name="BodyCells">Whether a row a body reads its own cell of moved: the population's scale row, or the state
/// row a kit or look assignment selects by.</param>
/// <param name="RefreshRefusal">Why a document value reading a moved row did not re-resolve, or
/// <see langword="null"/>.</param>
public readonly record struct WorldArenaPublication(WorldDefinition Definition, bool Moved, bool DriveGate, bool BodyCells, string? RefreshRefusal);
