using System.Text;

namespace Puck.Snapshots;

/// <summary>
/// The de-forked core of the hash-divergence localizer — the fine half of the two-stage determinism story ("hashing is
/// the coarse detector, full-state diff the fine localizer, used in sequence"). Both cores' Post batteries wrap this with
/// their own lockstep-stepping loop and core-specific report framing, but the byte-walk that turns "somewhere diverged"
/// into "component 'bus', byte offset 8192" is one shared implementation over a snapshot's flat bytes and section table.
/// </summary>
public static class SnapshotDivergence {
    /// <summary>
    /// Walks two snapshots' flat bytes to the first differing byte and maps that absolute offset back to its owning
    /// section via the section table. Returns <see langword="null"/> when the byte contents are identical (a caller
    /// reaches this only when some other signal already said the snapshots differ, so <see langword="null"/> means the
    /// divergence is in identity/captured-instant, not in the state bytes).
    /// </summary>
    /// <param name="a">The first snapshot's flat bytes.</param>
    /// <param name="b">The second snapshot's flat bytes.</param>
    /// <param name="sections">The first snapshot's section table (both snapshots share the same layout).</param>
    /// <returns>The owning section name, the offset within that section, and the absolute offset; or
    /// <see langword="null"/> when the byte contents are identical.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> is <see langword="null"/>.</exception>
    public static (string Section, int OffsetInSection, int AbsoluteOffset)? FindFirstDifference(
        ReadOnlySpan<byte> a,
        ReadOnlySpan<byte> b,
        IReadOnlyList<SnapshotSection> sections
    ) {
        ArgumentNullException.ThrowIfNull(argument: sections);

        var shared = Math.Min(val1: a.Length, val2: b.Length);

        for (var offset = 0; (offset < shared); ++offset) {
            if (a[offset] != b[offset]) {
                var section = FindSection(sections: sections, offset: offset);

                return ((section?.Name ?? "(unsectioned)"), (offset - (section?.Offset ?? 0)), offset);
            }
        }

        return ((a.Length != b.Length)
            ? ("(length)", shared, shared)
            : null);
    }

    /// <summary>Formats a short hex window of a snapshot's bytes around one offset, bracketing the byte at
    /// <paramref name="offset"/> — the both-sides detail a divergence report prints under its one-line localization.</summary>
    /// <param name="label">A one-character label for the side (e.g. <c>"A"</c>).</param>
    /// <param name="data">The snapshot bytes.</param>
    /// <param name="offset">The absolute offset to center and bracket.</param>
    /// <returns>The formatted window line.</returns>
    public static string FormatHexWindow(string label, ReadOnlySpan<byte> data, int offset) {
        const int windowBefore = 4;
        const int windowAfter = 12;
        var start = Math.Max(val1: 0, val2: (offset - windowBefore));
        var end = Math.Min(val1: data.Length, val2: (offset + windowAfter));
        var line = new StringBuilder(capacity: ((end - start) * 3));

        for (var index = start; (index < end); ++index) {
            if (index == offset) {
                _ = line.Append(value: '[').Append(value: data[index].ToString(format: "X2")).Append(value: ']');
            } else {
                _ = line.Append(value: ' ').Append(value: data[index].ToString(format: "X2"));
            }
        }

        return $"    {label} @0x{start:X6}: {line}";
    }

    private static SnapshotSection? FindSection(IReadOnlyList<SnapshotSection> sections, int offset) {
        foreach (var section in sections) {
            if ((offset >= section.Offset) && (offset < (section.Offset + section.Length))) {
                return section;
            }
        }

        return null;
    }
}
