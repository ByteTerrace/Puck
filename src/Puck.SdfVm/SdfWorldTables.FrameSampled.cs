using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The frame's sampled table (sdfFrameSampled, SdfFrameSampled's layout): the sampled values the render passes read as they
// draw, written from the frame's program and sampled set into a region like every other table, which owes only the words
// that moved. A height field's entry holds its heights this frame and the frame the tables last packed, so a height change
// owes its changed heights once as this frame's and once more, the frame after, as the previous frame's, and a still field
// owes nothing. The mesh bake never reads it: a bake job samples only the snapshots its groups hashed, from its own table.
public sealed partial class SdfWorldTables {
    private const int FrameSampledRegionIndex = 11;

    private readonly GpuRegion m_frameSampledRegion;
    // The program and set the table was last packed from, each height field's heights it holds as this frame's, whether a
    // field moved in that pack, and the words of its next write.
    private SdfProgram? m_frameSampledProgram;
    private SdfSampledSet? m_frameSampledSet;
    private SdfSampledSnapshot?[] m_frameSampledCurrent = [];
    private bool m_frameSampledMoving;
    private uint[] m_frameSampledWords = [];
    // One more each time a pack moved a word of the table: the cadence signature folds it as geometry, so a frame whose
    // height fields moved renders.
    private ulong m_frameSampledRevision;

    /// <summary>Gets the words of the frame's sampled table the latest frame's pack moved, which its upload owes each ring
    /// slot: the height values that moved, as this frame's and as the previous frame's. Zero for a frame whose height
    /// fields stood still.</summary>
    public int FrameSampledMovedWords { get; private set; }
    /// <summary>Gets the words every pack of the frame's sampled table has moved, and how many packs moved any: their
    /// quotient is the words a moving frame owes.</summary>
    public (long Words, long Packs) FrameSampledMoved { get; private set; }

    /// <summary>Describes the frame's sampled table: its words, and the words its packs moved (<see cref="FrameSampledMoved"/>,
    /// the latest pack's last).</summary>
    /// <returns>One line.</returns>
    public string DescribeFrameSampled() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"frame-sampled words={(m_frameSampledRegion.ShadowBytes / sizeof(uint))} height-fields={m_frameSampledCurrent.Length} moved-words={FrameSampledMoved.Words} moving-packs={FrameSampledMoved.Packs} last-moved={FrameSampledMovedWords}");

    // Packs the frame's sampled table: a word per handle of the frame's program, then each height field's header and its
    // heights this frame and the frame before. A pack whose program and set match the last and whose fields stood still
    // writes nothing.
    private void PackFrameSampled(SdfFrame frame) {
        var program = frame.Program;
        var set = frame.Sampled;
        var heightFields = program.HeightFields;
        var programMoved = !ReferenceEquals(objA: program, objB: m_frameSampledProgram);

        FrameSampledMovedWords = 0;
        if (!programMoved && ReferenceEquals(objA: set, objB: m_frameSampledSet) && !m_frameSampledMoving) { return; }

        var handles = program.SampledFields.Count;
        var words = handles;

        foreach (var binding in heightFields) {
            words = checked(words + SdfFrameSampled.HeaderWords + (2 * binding.Field.Width * binding.Field.Depth));
        }
        words = Math.Max(val1: words, val2: 1);
        if (programMoved || (m_frameSampledCurrent.Length != heightFields.Count)) { m_frameSampledCurrent = new SdfSampledSnapshot?[heightFields.Count]; }
        if (m_frameSampledWords.Length < words) { m_frameSampledWords = new uint[words]; }

        var span = m_frameSampledWords.AsSpan(start: 0, length: words);
        var floats = MemoryMarshal.Cast<uint, float>(span: span);
        var at = handles;
        var moving = false;

        span.Fill(value: SdfFrameSampled.None);
        for (var index = 0; (index < heightFields.Count); index++) {
            var binding = heightFields[index];
            var field = binding.Field;
            var count = (field.Width * field.Depth);
            var now = set[binding.Handle];
            var before = (m_frameSampledCurrent[index] ?? now);

            span[binding.Handle] = (uint)at;
            span[at] = (uint)field.Width;
            span[(at + 1)] = (uint)field.Depth;
            floats[(at + 2)] = field.Origin.X;
            floats[(at + 3)] = field.Origin.Y;
            floats[(at + 4)] = field.Origin.Z;
            floats[(at + 5)] = field.Cell;
            at += SdfFrameSampled.HeaderWords;
            now.Values.CopyTo(destination: floats.Slice(start: at, length: count));
            before.Values.CopyTo(destination: floats.Slice(start: (at + count), length: count));
            at += (2 * count);
            moving |= !ReferenceEquals(objA: before, objB: now);
            m_frameSampledCurrent[index] = now;
        }

        var bytes = (words * sizeof(uint));

        if (bytes > m_frameSampledRegion.ShadowBytes) {
            Retire(replaced: m_frameSampledRegion.Grow(byteCount: (sizeof(uint) * GpuCapacity.Grown(current: (m_frameSampledRegion.ShadowBytes / sizeof(uint)),
                required: words, ceiling: (GpuRegion.MaxByteCount / sizeof(uint)), divisor: 2))));
            m_bindingRevision++;
        }

        var held = MemoryMarshal.Cast<byte, uint>(span: m_frameSampledRegion.Contents);
        var moved = 0;

        for (var word = 0; (word < words); word++) {
            if ((word >= held.Length) || (held[word] != span[word])) { moved++; }
        }
        if (m_frameSampledRegion.Write(bytes: MemoryMarshal.AsBytes(span: span), offset: 0)) { m_frameSampledRevision++; }
        FrameSampledMovedWords = moved;
        if (moved > 0) { FrameSampledMoved = ((FrameSampledMoved.Words + moved), (FrameSampledMoved.Packs + 1)); }
        m_frameSampledProgram = program;
        m_frameSampledSet = set;
        // A field that moved this pack owes its previous heights again at the next, when this pack's become them.
        m_frameSampledMoving = moving;
    }
}
