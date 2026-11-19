namespace Puck.World.Protocol;

/// <summary>Names one record of one delivery stream: the stream it belongs to, the span of that stream it was composed
/// in, and its place in the stream. The authority's cursor issues every value (<see cref="WorldDeliveryCursor.Stamp"/>),
/// and a spanned mirror's ingress accepts a record only from the stream it is bound to, at or past the generation it
/// holds and strictly after the last ordinal it took.</summary>
/// <remarks>The stream identity is minted at random per cursor and derived from no principal, policy or table, and the
/// generation moves only on a cut the recipient's own delivery shows (<see cref="WorldDeliveryCursor"/>), so neither
/// tells a recipient anything about state it cannot read. Delivery metadata, never simulation state: no hash,
/// checkpoint or replay reads it.</remarks>
/// <param name="Stream">The stream's opaque identity; zero for the authority's own unfiltered fan-out, which has one
/// span that never ends and is ordered by its single delivering thread.</param>
/// <param name="Generation">The stream's span: zero at admission, one more at each cut.</param>
/// <param name="Ordinal">The record's place in the stream, rising by one with every record the stream sends.</param>
public readonly record struct WorldStreamStamp(ulong Stream, long Generation, long Ordinal) {
    /// <summary>Gets the stamp of the authority's own unfiltered fan-out, which no ingress orders and no cut ends.</summary>
    public static WorldStreamStamp Unspanned => default;

    /// <summary>Gets whether the record belongs to a spanned stream, one whose ingress orders it and whose cuts end its
    /// spans.</summary>
    public bool IsSpanned => (Stream != 0UL);
}
