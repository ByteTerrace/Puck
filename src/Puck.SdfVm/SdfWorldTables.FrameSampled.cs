using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The frame's sampled table (sdfFrameSampled, SdfFrameSampled's layout): the sampled values the render passes read as they
// draw, written from the frame's program and sampled set into a region like every other table, which owes only the words
// that moved. A displacement's entry holds its values this frame and the frame the tables last packed, so a change owes
// its changed values once as this frame's and once more, the frame after, as the previous frame's, and a still
// displacement owes nothing. The mesh bake never reads it: a bake job samples only the snapshots its groups hashed, from its
// own table.
public sealed partial class SdfWorldTables {
    private const int FrameSampledRegionIndex = 11;

    private readonly GpuRegion m_frameSampledRegion;
    // The program and set the table was last packed from, each displacement's values it holds as this frame's, whether a
    // field moved in that pack, and the words of its next write.
    private SdfProgram? m_frameSampledProgram;
    private SdfSampledSet? m_frameSampledSet;
    private SdfSampledSnapshot?[] m_frameSampledCurrent = [];
    private bool m_frameSampledMoving;
    private uint[] m_frameSampledWords = [];
    // One more each time a pack moved a word of the table: the cadence signature folds it as geometry, so a frame whose
    // displacements moved renders.
    private ulong m_frameSampledRevision;

    /// <summary>Gets the words of the frame's sampled table the latest frame's pack moved, which its upload owes each ring
    /// slot: the displacement values that moved, as this frame's and as the previous frame's. Zero for a frame whose
    /// displacements stood still.</summary>
    public int FrameSampledMovedWords { get; private set; }
    /// <summary>Gets the words every pack of the frame's sampled table has moved, and how many packs moved any: their
    /// quotient is the words a moving frame owes.</summary>
    public (long Words, long Packs) FrameSampledMoved { get; private set; }

    /// <summary>Describes the frame's sampled table: its words, and the words its packs moved (<see cref="FrameSampledMoved"/>,
    /// the latest pack's last).</summary>
    /// <returns>One line.</returns>
    public string DescribeFrameSampled() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"frame-sampled words={(m_frameSampledRegion.ShadowBytes / sizeof(uint))} displacements={m_frameSampledCurrent.Length} moved-words={FrameSampledMoved.Words} moving-packs={FrameSampledMoved.Packs} last-moved={FrameSampledMovedWords}");

    // Packs the frame's sampled table: a word per handle of the frame's program, then each displacement's header and its
    // values this frame and the frame before. A pack whose program and set match the last and whose fields stood still
    // writes nothing.
    private void PackFrameSampled(SdfFrame frame) {
        var program = frame.Program;
        var set = frame.Sampled;
        var displacements = program.Displacements;
        var programMoved = !ReferenceEquals(objA: program, objB: m_frameSampledProgram);

        FrameSampledMovedWords = 0;
        if (!programMoved && ReferenceEquals(objA: set, objB: m_frameSampledSet) && !m_frameSampledMoving) { return; }

        var handles = program.SampledFields.Count;
        var words = handles;

        // Instances that carry one displacement share its handle, so its entry is written once, at its first binding: the
        // program numbers handles in the order their first bindings come (SdfProgram.Displacements), so a binding is first
        // exactly when its handle passes every handle before it.
        var nextHandle = 0;

        foreach (var binding in displacements) {
            if (binding.Handle < nextHandle) { continue; }
            nextHandle = (binding.Handle + 1);
            words = checked(words + SdfFrameSampled.HeaderWords + (2 * binding.Displacement.Values.Count));
        }
        words = Math.Max(val1: words, val2: 1);
        if (programMoved || (m_frameSampledCurrent.Length != displacements.Count)) { m_frameSampledCurrent = new SdfSampledSnapshot?[displacements.Count]; }
        if (m_frameSampledWords.Length < words) { m_frameSampledWords = new uint[words]; }

        var span = m_frameSampledWords.AsSpan(start: 0, length: words);
        var floats = MemoryMarshal.Cast<uint, float>(span: span);
        var at = handles;
        var moving = false;

        span.Fill(value: SdfFrameSampled.None);
        nextHandle = 0;
        for (var index = 0; (index < displacements.Count); index++) {
            var binding = displacements[index];

            if (binding.Handle < nextHandle) { continue; }
            nextHandle = (binding.Handle + 1);

            var count = binding.Displacement.Values.Count;
            var now = set[binding.Handle];
            var before = (m_frameSampledCurrent[index] ?? now);

            span[binding.Handle] = (uint)at;
            binding.Displacement.WriteHeader(header: span.Slice(start: at, length: SdfFrameSampled.HeaderWords));
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
                required: words, ceiling: (m_frameSampledRegion.MostBytes / sizeof(uint)), divisor: 2))), carryingRegion: m_frameSampledRegion);
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
        // A field that moved this pack owes its previous values again at the next, when this pack's become them.
        m_frameSampledMoving = moving;
        if (moved > 0) { NoteDisplacedRows(); }
    }
    // A displaced draw's vertices move inside a box that stands, so a pack that moved displacement values notes each
    // static displaced row as moved where it stands (conceals), which owes the world's shadows its rectangle at every entry.
    private void NoteDisplacedRows() {
        for (var row = 0; (row < m_stagedCount); row++) {
            ref readonly var draw = ref m_stagedDraws[row];

            if ((draw.Displacement is null) || (MotionSlotOf(in draw) >= 0)) { continue; }
            var live = RowSelection(row: row, box: out var box, glass: out var glass);

            NoteMotion(cause: SdfMotionCause.Draw, row: row, wasLive: live, before: in box, wasGlass: glass, isLive: live, after: in box, isGlass: glass,
                wasCaster: !draw.CastsNoShadow, isCaster: !draw.CastsNoShadow, conceals: true);
        }
    }
}
