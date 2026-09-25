namespace Puck.World.Protocol;

/// <summary>
/// The state half of the presentation view: the narrow reader every presentation binding reads a row through,
/// addressed by state catalog ordinal. It exposes one cell at a time as a <see cref="WorldStateSample"/> and nothing
/// else of the document, so a binding reader holds no document and cannot reach members its recipient may not
/// observe. The colocated client implements it over its delivered definition today; the presentation view the
/// runtime and delivery programme builds implements the same contract over its own transport.
/// </summary>
public interface IWorldStateView {
    /// <summary>Gets the presentation manifest of the installed document: every state read its presentation sections
    /// bind, compiled once per document.</summary>
    Puck.World.Client.WorldPresentationManifest Manifest { get; }

    /// <summary>Resolves a document-lane row name to its state catalog ordinal in the installed layout.</summary>
    /// <param name="rowName">The row's name.</param>
    /// <param name="ordinal">The row's ordinal, or -1 when the installed layout declares no such row.</param>
    /// <returns><see langword="true"/> when the row resolves.</returns>
    bool TryResolveRow(string rowName, out int ordinal);
    /// <summary>Reads one cell of a resolved row.</summary>
    /// <param name="ordinal">The row's state catalog ordinal, from <see cref="TryResolveRow"/> against the installed
    /// layout.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the row's slot cell.</param>
    /// <param name="target">Whether to read the stored truth rather than the eased follower of a cell carrying an
    /// easing trait.</param>
    /// <param name="tick">The simulation tick the read answers as of.</param>
    /// <param name="engineTick">The engine tick the read answers as of, which an advancing cell's value is computed
    /// at.</param>
    /// <param name="sample">The cell's value, its row's envelope, and whether its value is still moving over time.</param>
    /// <returns><see langword="true"/> when the row resolves; the sample's value holds no case when the row declares
    /// no cell under <paramref name="key"/>.</returns>
    bool TryRead(int ordinal, string? key, bool target, ulong tick, ulong engineTick, out WorldStateSample sample);
}
/// <summary>How a cell's read value moves between ticks with no write to its stored value.</summary>
public enum WorldStateMotion : byte {
    /// <summary>The value changes only when the stored value is written: no trait, or an easing follower at rest.</summary>
    Still,

    /// <summary>An easing follower that has not reached its stored target; it moves every tick until it rests.</summary>
    Easing,

    /// <summary>A value advancing at a rate over engine ticks; it moves every tick.</summary>
    Advancing,

    /// <summary>A cycle stepping its output over ticks; it moves in whole steps and never rests.</summary>
    Stepping,
}
/// <summary>One cell as the state view reads it.</summary>
/// <param name="Value">The cell's value, holding no case when the row declares no cell under the key.</param>
/// <param name="Min">The row's declared lower bound, or <see langword="null"/> when it declares none.</param>
/// <param name="Max">The row's declared upper bound, or <see langword="null"/> when it declares none.</param>
/// <param name="Motion">How the read value moves between ticks with no write.</param>
public readonly record struct WorldStateSample(CellValue Value, long? Min, long? Max, WorldStateMotion Motion);
