using System.Diagnostics.CodeAnalysis;

namespace Puck.World.Client;

/// <summary>
/// Keyed cells of authored state rows read as text through a <see cref="WorldStateMirror"/>: the cell read a radial's
/// labels and icons and a binding bar's action icons make. The seat registers these cells with its routed mirror
/// (<see cref="WorldPresentationManifest.SeatBindings"/>); a reader only looks their slots up, reading nothing and
/// registering nothing. Each row reference is parsed once and each row and key looked up once, so a reader that asks
/// every frame parses and allocates nothing after its first read of a cell; the kept answers are let go when the
/// reader is handed a different mirror or the mirror's registered bindings change (<see cref="WorldStateMirror.Generation"/>).
/// </summary>
public sealed class WorldStateCells {
    private readonly Dictionary<string, string?> m_rows = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Row, string Key), int> m_slots = [];

    private int m_generation;
    private WorldStateMirror? m_mirror;

    /// <summary>Reads a row's keyed cell as text.</summary>
    /// <param name="mirror">The mirror to read through, or <see langword="null"/> before the reader has one; every
    /// cell then reads absent.</param>
    /// <param name="rowReference">The authored row reference, <c>state.&lt;row&gt;</c>, or <see langword="null"/>.</param>
    /// <param name="key">The cell's key, or <see langword="null"/>.</param>
    /// <param name="slot">The mirror slot the cell reads through, so a caller can ask the mirror whether it moved; -1
    /// when there is no mirror, the reference names no row, the key is absent or empty, or no registration records the
    /// cell.</param>
    /// <param name="text">The cell's text when it holds one.</param>
    /// <returns><see langword="true"/> when the cell holds text.</returns>
    public bool TryText(WorldStateMirror? mirror, string? rowReference, string? key, out int slot, [NotNullWhen(returnValue: true)] out string? text) {
        slot = Slot(
            key: key,
            mirror: mirror,
            rowReference: rowReference
        );
        text = null;

        return (
            (slot >= 0) &&
            mirror!.TryText(
                slot: slot,
                value: out text
            )
        );
    }

    private int Slot(WorldStateMirror? mirror, string? rowReference, string? key) {
        if (
            (mirror is null) ||
            (rowReference is null) ||
            (key is not { Length: > 0 })
        ) {
            return -1;
        }
        if (
            !ReferenceEquals(
            objA: mirror,
            objB: m_mirror
        ) ||
            (mirror.Generation != m_generation)
        ) {
            m_mirror = mirror;
            m_generation = mirror.Generation;
            m_slots.Clear();
        }
        if (!m_rows.TryGetValue(
            key: rowReference,
            value: out var row
        )) {
            row = (WorldStateBindingContext.TryParseRowReference(
                reference: rowReference,
                rowName: out var parsed
            )
                ? parsed
                : null
            );
            m_rows[rowReference] = row;
        }
        if (row is null) {
            return -1;
        }
        if (!m_slots.TryGetValue(
            key: (row, key),
            value: out var slot
        )) {
            slot = mirror.SlotOf(
                binding: new StateBinding(
                    Key: key,
                    Row: row,
                    Target: false
                ),
                conversion: WorldStateConversion.Number
            );
            m_slots[(row, key)] = slot;
        }

        return slot;
    }
}
