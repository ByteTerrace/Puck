namespace Puck.World.Protocol;

/// <summary>
/// The stamp a state delivery carries beside the delivered definition: the tick and engine tick the delivered values
/// hold as of, and the document-lane row ordinals whose stored values moved since the previous delivery. A reader of
/// bound rows reads only the rows named here, plus the cells whose value-over-time trait is still moving.
/// </summary>
/// <param name="Tick">The simulation tick the delivered values hold as of — the tick the step in progress produces,
/// which the same step's snapshot carries.</param>
/// <param name="EngineTick">The engine tick the delivered values hold as of, which a <c>StateAdvance</c> cell's
/// value is computed at.</param>
/// <param name="MovedRows">The state catalog ordinals of the rows whose stored values moved, each at most once, in no
/// particular order. The memory belongs to the sender and is valid only for the duration of the delivery call.</param>
/// <param name="Everything">Whether every row must be treated as moved: the sender could not name the moved rows
/// (the state arena was rebuilt).</param>
public readonly record struct WorldStateStamp(ulong Tick, ulong EngineTick, ReadOnlyMemory<int> MovedRows, bool Everything);
