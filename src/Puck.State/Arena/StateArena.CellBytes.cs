using Puck.Maths;
using System.Buffers;
using System.Runtime.InteropServices;
using Puck.Assets;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Visits the next held cell exactly as <see cref="TryNextCell"/> does and also
    /// answers the storage slot the walk found it in, which <see cref="WriteCell"/> and <see cref="SlotHoldsValue"/>
    /// read without resolving the key again.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cursor">Start at zero; advances as <see cref="TryNextCell"/>'s does.</param>
    /// <param name="key">The held cell's key, or the invalid default on exhaustion.</param>
    /// <param name="slot">The held cell's storage slot, or <c>-1</c> on exhaustion. A structural edit or a relayout
    /// invalidates it.</param>
    /// <returns><see langword="true"/> when another held cell exists.</returns>
    public bool TryNextCellSlot(int rowOrdinal, ref int cursor, out CellKey key, out int slot) => TryNextSlot(
        cursor: ref cursor,
        key: out key,
        rowOrdinal: rowOrdinal,
        slot: out slot
    );
    /// <summary>Returns whether a storage slot holds a value: the answer <see cref="TryRead(int, CellKey, out CellValue)"/>
    /// gives for the key the slot answers to.</summary>
    /// <param name="slot">A storage slot <see cref="TryNextCellSlot"/> or
    /// <see cref="TryCellSlot"/> answered.</param>
    /// <returns><see langword="true"/> when the slot holds a value.</returns>
    public bool SlotHoldsValue(int slot) => ((((uint)slot) < ((uint)m_layout.CellSlotCount)) && BitWords.Contains(index: slot, words: m_presence));
    /// <summary>Returns whether the arena stores a row's cells in its columns. A row it does not store (a host-owned
    /// row, whose facet owns its values) holds no cell here: <see cref="TryNextCell"/> visits none and
    /// <see cref="ToRow"/> answers the declaration.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns><see langword="true"/> when the row's cells live in the arena's columns.</returns>
    public bool StoresCells(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_layout.RowCount)) && m_layout[rowOrdinal].IsStored);

    /// <summary>Appends the canonical bytes of one stored cell: its key's name, whether it holds a value, its value,
    /// its behaviour override, its clock, its observation stamp, its own audience restriction and the trait it
    /// declares for itself. Two cells write the same bytes exactly when a reader of both is shown the same cell,
    /// whatever arena, key table or slot holds each, so a digest of the bytes identifies the cell's content.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="slot">The cell's storage slot, as <see cref="TryNextCellSlot"/>
    /// answered it.</param>
    /// <remarks>Allocates nothing. Text is written as its UTF-16 code units, so the bytes are compared and hashed,
    /// never parsed back.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowOrdinal"/> names no stored row, or
    /// <paramref name="slot"/> is not one of its slots.</exception>
    public void WriteCell(IBufferWriter<byte> writer, int rowOrdinal, int slot) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        if ((((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) || !m_layout[rowOrdinal].IsStored) {
            throw new ArgumentOutOfRangeException(actualValue: rowOrdinal, message: "The ordinal names no stored row.", paramName: nameof(rowOrdinal));
        }
        ref readonly var layout = ref m_layout[rowOrdinal];

        if ((slot < layout.CellStart) || (slot >= (layout.CellStart + layout.CellCapacity))) {
            throw new ArgumentOutOfRangeException(actualValue: slot, message: "The slot is not one of the row's.", paramName: nameof(slot));
        }

        var named = m_keys.TryGetName(key: KeyAtSlot(layout: in layout, slot: slot), name: out var name);
        var clock = HasClock(slot: slot);
        var stamp = m_observations?[slot];
        var visibility = m_visibilities?[slot];
        var declared = EffectiveBehavior.None;
        var declares = (named && layout.HasTraits && m_catalog.TryDeclaredBehavior(declared: out declared, key: name, rowOrdinal: rowOrdinal));

        writer.WriteCodeUnits(value: (named ? name.Value : string.Empty));
        writer.WriteByte(value: ((byte)(
            (BitWords.Contains(index: slot, words: m_presence) ? CellHolds : 0) | (clock ? CellClock : 0) | ((stamp is not null) ? CellStamp : 0) |
            ((visibility is not null) ? CellVisibility : 0) | (declares ? CellTrait : 0))));
        writer.WriteByte(value: ((byte)layout.Kind));
        switch (layout.Kind) {
            case CellKind.Text:
                WriteOptionalText(writer: writer, value: m_texts?[slot]);

                break;
            case CellKind.Vector:
                writer.WriteVarUInt(value: ((uint)layout.Dimensions));
                writer.WriteBytes(value: MemoryMarshal.Cast<sbyte, byte>(span: VectorMemory(layout: in layout, slot: slot).Span));

                break;
            default:
                writer.WriteUInt64(value: ((ulong)m_numbers[slot]));

                break;
        }
        writer.WriteByte(value: ((m_behaviors is null) ? ((byte)StateCellBehavior.Inherit) : m_behaviors[slot]));
        if (clock) {
            writer.WriteUInt64(value: ((ulong)ReadNumberRaw(column: ArenaColumn.ClockEpochTick, index: slot)));
            writer.WriteUInt64(value: ((ulong)ReadNumberRaw(column: ArenaColumn.ClockEpochEngineTick, index: slot)));
            writer.WriteUInt64(value: ((ulong)ReadNumberRaw(column: ArenaColumn.ClockY0, index: slot)));
            writer.WriteUInt64(value: ((ulong)ReadNumberRaw(column: ArenaColumn.ClockV0, index: slot)));
            writer.WriteUInt64(value: ((ulong)ReadNumberRaw(column: ArenaColumn.ClockSubstepTicks, index: slot)));
        }
        if (stamp is not null) {
            writer.WriteUInt64(value: ((ulong)stamp.Tick));
            writer.WriteByte(value: (stamp.Visible ? ((byte)1) : ((byte)0)));
        }
        if (visibility is not null) { WriteVisibility(writer: writer, visibility: visibility); }
        if (declares) { WriteBehavior(behavior: declared, writer: writer); }
    }
    /// <summary>Appends the canonical bytes of a value-over-time trait, or of none: which trait it is and every
    /// parameter it carries.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="behavior">The trait, or <see cref="EffectiveBehavior.None"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void WriteBehavior(IBufferWriter<byte> writer, EffectiveBehavior behavior) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        if (behavior.Advance is { } advance) {
            writer.WriteByte(value: 1);
            writer.WriteUInt64(value: ((ulong)advance.PerSecondNumerator));
            writer.WriteUInt64(value: ((ulong)advance.PerSecondDenominator));
        } else if (behavior.Dynamics is { } dynamics) {
            writer.WriteByte(value: 2);
            writer.WriteCodeUnits(value: dynamics.Row);
        } else if (behavior.Cycle is { } cycle) {
            writer.WriteByte(value: 3);
            var word = cycle.Word;

            writer.WriteVarUInt(value: ((word is null) ? 0U : checked((uint)(word.Count + 1))));
            for (var index = 0; (index < (word?.Count ?? 0)); index++) { writer.WriteUInt64(value: ((ulong)word![index])); }
            writer.WriteUInt64(value: ((ulong)cycle.Power));
            writer.WriteByte(value: ((byte)cycle.Output));
            writer.WriteUInt64(value: ((ulong)cycle.TicksPerStep));
        } else {
            writer.WriteByte(value: 0);
        }
    }

    private const int CellHolds = 1;
    private const int CellClock = 2;
    private const int CellStamp = 4;
    private const int CellVisibility = 8;
    private const int CellTrait = 16;

    // The fields StateVisibilityHash.Append folds, in its order: an absent reader list and an empty one differ.
    private static void WriteVisibility(IBufferWriter<byte> writer, StateVisibility visibility) {
        writer.WriteByte(value: ((byte)visibility.Hidden));
        WriteOptionalText(writer: writer, value: visibility.ReadersFrom);
        var readers = visibility.Readers;

        writer.WriteVarUInt(value: ((readers is null) ? 0U : checked((uint)(readers.Count + 1))));
        for (var index = 0; (index < (readers?.Count ?? 0)); index++) { writer.WriteCodeUnits(value: readers![index]); }
    }
    private static void WriteOptionalText(IBufferWriter<byte> writer, string? value) {
        writer.WriteByte(value: ((value is null) ? ((byte)0) : ((byte)1)));
        if (value is not null) { writer.WriteCodeUnits(value: value); }
    }
}
