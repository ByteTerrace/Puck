using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>A delivered record disclosed only to the input owner it names (<see cref="WorldOwnedRecords.ForOwner"/>).</summary>
public interface IWorldOwnedRecord {
    /// <summary>Gets the input owner the record belongs to.</summary>
    Principal Owner { get; }
}
/// <summary>Discloses a delivery's owner-scoped records to one recipient.</summary>
public static class WorldOwnedRecords {
    /// <summary>Returns the one record a recipient owns, without allocating on steady delivery.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="records">Every owner's records, at most one each.</param>
    /// <param name="owner">The recipient.</param>
    /// <param name="scratch">The recipient's reused one-element buffer, grown on first use.</param>
    /// <returns>The recipient's record, or empty when the delivery carries none for it.</returns>
    public static ReadOnlyMemory<T> ForOwner<T>(ReadOnlyMemory<T> records, Principal owner, ref T[] scratch) where T : struct, IWorldOwnedRecord {
        // By reference: a publication carries a whole baseline image, and the scan visits every owner's.
        foreach (ref readonly var record in records.Span) {
            if (record.Owner != owner) { continue; }
            if (scratch.Length == 0) { scratch = new T[1]; }
            scratch[0] = record;
            return scratch.AsMemory(length: 1, start: 0);
        }
        return ReadOnlyMemory<T>.Empty;
    }
}
/// <summary>One authoritative finite seat/session state cell.</summary>
public readonly record struct WorldInputSeatState(string Family, string State);
/// <summary>The authority's source decisions at an input epoch boundary. Source memory is immutable.</summary>
public readonly record struct WorldInputPublication(Principal Owner, int Body, int Generation, uint Revision, ulong Epoch,
    ushort Mapping, PlayerIntent Baseline, ReadOnlyMemory<WorldInputSourceState> Sources, WorldInputSwitch Boundary,
    ReadOnlyMemory<WorldInputSeatState> SeatStates) : IWorldOwnedRecord;
/// <summary>The last input the authority answered for one owner, taken or refused: what tells a client that an input it
/// sent has had its answer, where an acknowledgement that a submission arrived does not. Disclosed only to its owner.</summary>
/// <param name="Owner">The input owner.</param>
/// <param name="Epoch">The input epoch of the batch answered.</param>
/// <param name="Sequence">The highest event sequence of the last batch with events the authority took or refused in that
/// epoch.</param>
/// <param name="Tick">The simulation tick that batch was answered in.</param>
public readonly record struct WorldInputAnswer(Principal Owner, ulong Epoch, uint Sequence, ulong Tick) : IWorldOwnedRecord;
/// <summary>The exact continuation of the body an owner drives, as the authority held it after a tick: what a client
/// restores its prediction of its own body from before replaying the input the authority had not yet taken. Disclosed
/// only to its owner.</summary>
/// <param name="Owner">The input owner whose body it is.</param>
/// <param name="Tick">The simulation tick the continuation was captured after.</param>
/// <param name="EngineTick">The engine-tick boundary that tick completed at, where the body's next step starts.</param>
/// <param name="Body">The body's 0-based index at the authority.</param>
/// <param name="Generation">The body slot's activation generation.</param>
/// <param name="Entry">The body's population entry in the authority checkpoint's own entry encoding; the memory is
/// immutable.</param>
public readonly record struct WorldOwnBody(Principal Owner, ulong Tick, ulong EngineTick, int Body, int Generation, ReadOnlyMemory<byte> Entry) : IWorldOwnedRecord;
