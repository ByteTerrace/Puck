using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>One authoritative finite seat/session state cell.</summary>
public readonly record struct WorldInputSeatState(string Family, string State);
/// <summary>The authority's source decisions at an input epoch boundary. Source memory is immutable.</summary>
public readonly record struct WorldInputPublication(Principal Owner, int Body, int Generation, uint Revision, ulong Epoch,
    ushort Mapping, PlayerIntent Baseline, ReadOnlyMemory<WorldInputSourceState> Sources, WorldInputSwitch Boundary,
    ReadOnlyMemory<WorldInputSeatState> SeatStates) {
    /// <summary>Discloses continuation only to its admitted owner, without allocating on steady delivery.</summary>
    public static ReadOnlyMemory<WorldInputPublication> ForOwner(ReadOnlyMemory<WorldInputPublication> publications, Principal owner,
        ref WorldInputPublication[] scratch) {
        foreach (var publication in publications.Span) {
            if (publication.Owner != owner) { continue; }
            if (scratch.Length == 0) { scratch = new WorldInputPublication[1]; }
            scratch[0] = publication;
            return scratch.AsMemory(length: 1, start: 0);
        }
        return ReadOnlyMemory<WorldInputPublication>.Empty;
    }
}
