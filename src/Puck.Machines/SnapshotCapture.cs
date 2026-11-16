namespace Puck.Machines;

/// <summary>
/// Writes a machine's state through its retained <see cref="StateWriter"/> and records the byte range each named part
/// occupies, which is how every core builds its <see cref="SnapshotImage"/>: the section table rides beside the
/// serialized bytes, so a divergence localizer can map a raw offset back to the part that owns it. The invariant is that
/// the sections returned by <see cref="Finish"/> are contiguous from offset zero and cover every written byte, because
/// <see cref="EndSection"/> is the only way a range is recorded and it always starts where the previous one ended.
/// </summary>
public struct SnapshotCapture {
    private readonly SnapshotSection[] m_sections;
    private readonly StateWriter m_writer;

    private int m_count;
    private int m_offset;

    /// <summary>Initializes a new instance of the <see cref="SnapshotCapture"/> struct, rewinding the writer so repeated
    /// captures reuse one backing buffer.</summary>
    /// <param name="writer">The machine's retained writer; the capture resets it.</param>
    /// <param name="sectionCount">The number of sections the capture will record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sectionCount"/> is negative.</exception>
    public SnapshotCapture(StateWriter writer, int sectionCount) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        ArgumentOutOfRangeException.ThrowIfNegative(value: sectionCount);

        writer.Reset();

        m_count = 0;
        m_offset = 0;
        m_sections = new SnapshotSection[sectionCount];
        m_writer = writer;
    }

    /// <summary>Gets the writer a part serializes itself into.</summary>
    public readonly StateWriter Writer => m_writer;

    /// <summary>Records everything written since the previous section ended as one section.</summary>
    /// <param name="name">The section's name.</param>
    /// <exception cref="InvalidOperationException">Every declared section has been recorded.</exception>
    public void EndSection(string name) {
        if (m_count >= m_sections.Length) {
            throw new InvalidOperationException(message: "The capture recorded more sections than it declared.");
        }

        m_sections[m_count++] = new SnapshotSection(
            Name: name,
            Offset: m_offset,
            Length: (m_writer.Length - m_offset)
        );
        m_offset = m_writer.Length;
    }
    /// <summary>Serializes each component in order, recording one section apiece.</summary>
    /// <param name="components">The components, in save order.</param>
    /// <param name="names">The section name of each component, one-to-one by index.</param>
    /// <exception cref="ArgumentException"><paramref name="names"/> is shorter than <paramref name="components"/>.</exception>
    /// <exception cref="InvalidOperationException">Every declared section has been recorded.</exception>
    public void Save(ReadOnlySpan<ISnapshotable> components, ReadOnlySpan<string> names) {
        if (names.Length < components.Length) {
            throw new ArgumentException(
                message: "Every component needs a section name.",
                paramName: nameof(names)
            );
        }

        for (var index = 0; (index < components.Length); ++index) {
            components[index].SaveState(writer: m_writer);
            EndSection(name: names[index]);
        }
    }
    /// <summary>Takes ownership of one exact-size copy of the written bytes, the only per-capture allocation besides the
    /// section table, and pairs it with the section table.</summary>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidOperationException">Fewer sections were recorded than declared, or bytes were written
    /// after the last section ended.</exception>
    public readonly SnapshotImage Finish() {
        if (
            (m_count != m_sections.Length) ||
            (m_offset != m_writer.Length)
        ) {
            throw new InvalidOperationException(message: "The capture's sections do not cover the written bytes exactly.");
        }

        return new SnapshotImage(
            data: m_writer.ToArray(),
            sections: m_sections
        );
    }
}
