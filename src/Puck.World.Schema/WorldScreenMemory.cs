using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Which way a <see cref="WorldScreenMemory"/> binding moves a byte between a machine's bus and a
/// <c>state.world</c> cell.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldScreenMemoryDirection>))]
public enum WorldScreenMemoryDirection : byte {
    /// <summary>Machine bus to cell: the binding mirrors the machine's live bytes into the cell every tick, writing
    /// the cell only when the peeked value changed since the last mirror.</summary>
    Read,
    /// <summary>Cell to machine bus: the binding pokes the cell's own value into the machine's bus every tick the
    /// cell's value has moved since the last poke.</summary>
    Write,
}
/// <summary>
/// One live byte-window binding between a declared <c>screens</c> row's booted machine and an ordinary
/// <c>state.world</c> Int cell — the campfire seam: a 3D log on the fire is a cell, the cartridge reads it (or, the
/// other direction, a lever the cartridge throws is a cell the 3D world reads). A <see cref="Direction"/> of
/// <see cref="WorldScreenMemoryDirection.Read"/> mirrors the machine's bytes into the cell every tick through the
/// ordinary state-mutation door (<c>WorldMutation.UpsertStateCell</c>, the same door a rule's own single-cell write
/// folds into — see <c>Server.WorldServer.MachineMemory.cs</c>), a write landing only when the peeked value changed,
/// so a quiet machine costs nothing; <see cref="WorldScreenMemoryDirection.Write"/> pokes the cell's current value
/// into the machine's bus (<see cref="Puck.Abstractions.Machines.IMachineMemoryPeek.PokeByte"/> through
/// <c>Server.IWorldMachineHost.TryPokeMessage</c>) every tick the cell's value has moved since the last poke — a
/// mutation of the machine's guest state through the host seam, deterministic under replay because the value it
/// carries is itself simulation state a replay reproduces bit-for-bit, unlike a bare debug poke's own unrecorded-input
/// caveat. Distinct from an addon row's own <c>WorldAddonMemoryWatch</c> (an edge-triggered event feed for a mounted
/// guest): this binding is a standing per-tick mirror into ordinary state, read by every other state consumer
/// (rules, HUD bindings, a placement's <c>respond</c>) with no addon in the loop.
/// </summary>
/// <param name="Address">The machine bus address the window starts at. Validated within
/// <c>0..(<see cref="MaxAddress"/> - Width + 1)</c> — outside the engine's addressable memory refuses by name at
/// validation, never at runtime (a machine's own <c>IMachineMemoryPeek</c> silently reads/no-ops out of its own
/// smaller readable/writable range instead, exactly as it does for any other peek/poke).</param>
/// <param name="Width">How many bytes the window spans, little-endian (the low byte at <paramref name="Address"/>):
/// 1 or 2.</param>
/// <param name="Row">The declared <c>state.world</c> row this binding mirrors to/from — must resolve to a kind=Int
/// row.</param>
/// <param name="Key">The cell inside <paramref name="Row"/>, or <see langword="null"/> for its slot cell. Refused
/// when <paramref name="Row"/> is keyed and this is absent, or unkeyed and this is present — the same (row, key)
/// pair rule every other named-cell reference in this document follows. Omitted from the wire when null.</param>
/// <param name="Direction">Which way the binding moves a value.</param>
public sealed record WorldScreenMemory(
    int Address,
    int Width,
    string Row,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
    WorldScreenMemoryDirection Direction = WorldScreenMemoryDirection.Read
) {
    /// <summary>The highest bus address a binding's window may reach — the SM83 family's full 16-bit bus view
    /// (<c>0x0000</c>-<c>0xFFFF</c>), the one shipped memory-peek-capable engine today. A future engine with a wider
    /// bus raises this by name when it needs to.</summary>
    public const int MaxAddress = 0xFFFF;
}
