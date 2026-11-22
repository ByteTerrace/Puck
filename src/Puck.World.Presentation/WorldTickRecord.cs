using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The boot world's in-session history window as a frame reads it: its oldest and newest ticks, the tick the
/// world sits at, and the window's length.</summary>
/// <param name="Oldest">The oldest tick the window holds.</param>
/// <param name="Head">The newest tick the window holds.</param>
/// <param name="Cursor">The tick the world sits at.</param>
/// <param name="WindowTicks">The window's length, in ticks.</param>
public readonly record struct WorldHistoryWindow(ulong Oldest, ulong Head, ulong Cursor, ulong WindowTicks);

/// <summary>
/// The boot authority's values a frame reads, copied once a host step at its collect, on the pump inside the boundary
/// window, and published whole (<see cref="WorldPresentationFeed.Tick"/>). Immutable: a reader on any thread sees one
/// collect's values, never a field the next tick is writing.
/// </summary>
/// <param name="Tick">The boot world's completed tick when the record was copied.</param>
/// <param name="DocumentVersion">The version of the document the boot authority had installed.</param>
/// <param name="SimulatedBodies">The bodies the boot world simulates.</param>
/// <param name="PeerCapacity">The bodies the boot world can hold beside its local seats.</param>
/// <param name="History">The boot world's in-session history window, or <see langword="null"/> while it holds
/// none.</param>
/// <param name="SeatPlaces">Each local seat body's innermost place in the boot world, or <see langword="null"/> for a
/// seat in none, indexed by 0-based seat.</param>
/// <param name="Commands">The live document's authored commands, which the console registry answers beside the
/// engine's verbs.</param>
public sealed record WorldTickRecord(ulong Tick, WorldDocumentVersion DocumentVersion, int SimulatedBodies, int PeerCapacity,
    WorldHistoryWindow? History, IReadOnlyList<string?> SeatPlaces, AuthoredCommandSet Commands) {
    /// <summary>Gets the record a feed holds before its first collect: tick zero, no bodies, no history, no places and no
    /// authored commands.</summary>
    public static WorldTickRecord Empty { get; } = new(Commands: AuthoredCommandSet.Empty, DocumentVersion: default, History: null, PeerCapacity: 0, SeatPlaces: [], SimulatedBodies: 0, Tick: 0UL);

    /// <summary>Reads a local seat's place.</summary>
    /// <param name="seat">The 0-based seat.</param>
    /// <returns>The seat body's innermost place, or <see langword="null"/> for a seat in none or out of range.</returns>
    public string? SeatPlace(int seat) => ((((uint)seat) < ((uint)SeatPlaces.Count)) ? SeatPlaces[seat] : null);
}
