using System.Diagnostics.CodeAnalysis;

namespace Puck.World.Client;

/// <summary>
/// Keyed cells of authored state rows read as text through a <see cref="WorldStateMirror"/>: the cell read a radial's
/// labels and icons and a binding bar's action icons make. Each row reference is parsed once, and each row and key is
/// registered with the mirror once and its slot kept, so a reader that asks every frame parses, registers and
/// allocates nothing after its first read of a cell. A mirror's registered slots last as long as the mirror, through
/// every document it installs; the kept slots are let go when the reader is handed a different mirror.
/// </summary>
public sealed class WorldStateCells {
    private readonly Dictionary<string, string?> m_rows = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<(string Row, string Key), int> m_slots = [];

    private WorldStateMirror? m_mirror;

    /// <summary>Reads a row's keyed cell as text.</summary>
    /// <param name="mirror">The mirror to read through, or <see langword="null"/> before the reader has one; every
    /// cell then reads absent.</param>
    /// <param name="rowReference">The authored row reference, <c>state.&lt;row&gt;</c>, or <see langword="null"/>.</param>
    /// <param name="key">The cell's key, or <see langword="null"/>.</param>
    /// <param name="slot">The mirror slot the cell reads through, so a caller can ask the mirror whether it moved; -1
    /// when there is no mirror, the reference names no row, or the key is absent or empty.</param>
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
        if (!ReferenceEquals(
            objA: mirror,
            objB: m_mirror
        )) {
            m_mirror = mirror;
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
            slot = mirror.Register(
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
