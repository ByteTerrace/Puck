namespace Puck.State;

/// <summary>One host fact a read schedule depends on: a source the host versions (a body's position, its facts, the
/// live-body roster), read for one body, for the body the evaluation in flight binds to <c>$each</c>, or source-wide.
/// The host names its sources by small integers of its own (<see cref="IStateReader.TryScheduleFact"/>), answers
/// their versions (<see cref="IStateReader.TryFactVersion"/>) and reports which moved
/// (<see cref="IStateReader.DrainFactMoves"/>).</summary>
/// <param name="Source">The host's source number.</param>
/// <param name="Body">The body index; <see cref="EachBody"/> for the body bound to <c>$each</c>; <see cref="Whole"/>
/// for the source as a whole.</param>
public readonly record struct FactRead(int Source, int Body) {
    /// <summary>The <see cref="Body"/> of a read made for the body the evaluation in flight binds to <c>$each</c>.</summary>
    public const int EachBody = -1;
    /// <summary>The <see cref="Body"/> of a read of a whole source, such as the roster a body sweep iterates.</summary>
    public const int Whole = -2;
}
