using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// The frame's sampled table (sdfFrameSampled, SdfFrameSampled's layout): the sampled values the render passes read as they
// draw and the frame's form bands, written from the frame's program, sampled set and bands into a region like every other
// table, which owes only the words that moved. A form band's draw rows are named after the frame's draws are staged
// (PackFormBandRows), so they name the rows the kernels read this frame. A displacement's entry holds its values this frame and the frame the tables last packed, so a change owes
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
    // The form bands the table was last packed from, their live part sorted by first slot, the word they start at and
    // how many there are, whether their draw rows are still owed this frame, and the rows each names.
    private IReadOnlyList<SdfFormBand>? m_frameSampledBands;
    private SdfFormBand[] m_formBandScratch = [];
    private int m_formBandBase;
    private int m_formBandCount;
    private bool m_formBandRowsOwed;
    private uint[] m_formBandRows = [];
    private int[] m_formBandRowCounts = [];

    /// <summary>Gets the words of the frame's sampled table the latest frame's pack moved, which its upload owes each ring
    /// slot: the displacement values that moved, as this frame's and as the previous frame's. Zero for a frame whose
    /// displacements stood still.</summary>
    public int FrameSampledMovedWords { get; private set; }
    /// <summary>Gets the motes the latest packed frame's form bands generate together (<see cref="SdfFormBand.MoteCount"/>,
    /// at most <see cref="SdfFormBand.MaxMotes"/>), which the mote pass dispatches one invocation each; zero when no form is
    /// changing.</summary>
    public int FormMoteTotal { get; private set; }
    /// <summary>Gets the words every pack of the frame's sampled table has moved, and how many packs moved any: their
    /// quotient is the words a moving frame owes.</summary>
    public (long Words, long Packs) FrameSampledMoved { get; private set; }

    /// <summary>Describes the frame's sampled table: its words, and the words its packs moved (<see cref="FrameSampledMoved"/>,
    /// the latest pack's last).</summary>
    /// <returns>One line.</returns>
    public string DescribeFrameSampled() => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
        handler: $"frame-sampled words={(m_frameSampledRegion.ShadowBytes / sizeof(uint))} displacements={m_frameSampledCurrent.Length} form-bands={m_formBandCount} motes={FormMoteTotal} moved-words={FrameSampledMoved.Words} moving-packs={FrameSampledMoved.Packs} last-moved={FrameSampledMovedWords}");

    // Packs the frame's sampled table: a word per handle of the frame's program, then each displacement's header and its
    // values this frame and the frame before. A pack whose program and set match the last and whose fields stood still
    // writes nothing.
    private void PackFrameSampled(SdfFrame frame) {
        var program = frame.Program;
        var set = frame.Sampled;
        var displacements = program.Displacements;
        var programMoved = !ReferenceEquals(objA: program, objB: m_frameSampledProgram);
        var bands = frame.FormBands;

        FrameSampledMovedWords = 0;
        if (!programMoved && ReferenceEquals(objA: set, objB: m_frameSampledSet) && !m_frameSampledMoving && ReferenceEquals(objA: bands, objB: m_frameSampledBands)) { return; }

        var handles = program.SampledFields.Count;
        var words = (SdfFrameSampled.TableHeaderWords + handles);

        // Instances that carry one displacement share its handle, so its entry is written once, at its first binding: the
        // program numbers handles in the order their first bindings come (SdfProgram.Displacements), so a binding is first
        // exactly when its handle passes every handle before it.
        var nextHandle = 0;

        foreach (var binding in displacements) {
            if (binding.Handle < nextHandle) { continue; }
            nextHandle = (binding.Handle + 1);
            words = checked(words + SdfFrameSampled.HeaderWords + (2 * binding.Displacement.Values.Count));
        }

        var bandCount = SortFormBands(bands: bands);
        var bandBase = words;

        words = checked(words + (bandCount * SdfFrameSampled.BandWords));
        if (programMoved || (m_frameSampledCurrent.Length != displacements.Count)) { m_frameSampledCurrent = new SdfSampledSnapshot?[displacements.Count]; }
        if (m_frameSampledWords.Length < words) { m_frameSampledWords = new uint[words]; }

        var span = m_frameSampledWords.AsSpan(start: 0, length: words);
        var floats = MemoryMarshal.Cast<uint, float>(span: span);
        var at = (SdfFrameSampled.TableHeaderWords + handles);
        var moving = false;

        span.Fill(value: SdfFrameSampled.None);
        span[SdfFrameSampled.TableBandCountWord] = (uint)bandCount;
        span[SdfFrameSampled.TableBandBaseWord] = (uint)bandBase;
        span[SdfFrameSampled.TableMoteTotalWord] = PackFormBands(words: span.Slice(start: bandBase, length: (bandCount * SdfFrameSampled.BandWords)), count: bandCount);
        FormMoteTotal = (int)span[SdfFrameSampled.TableMoteTotalWord];
        span[3] = 0u;
        nextHandle = 0;
        for (var index = 0; (index < displacements.Count); index++) {
            var binding = displacements[index];

            if (binding.Handle < nextHandle) { continue; }
            nextHandle = (binding.Handle + 1);

            var count = binding.Displacement.Values.Count;
            var now = set[binding.Handle];
            var before = (m_frameSampledCurrent[index] ?? now);

            span[(SdfFrameSampled.TableHeaderWords + binding.Handle)] = (uint)at;
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
        m_frameSampledBands = bands;
        m_formBandBase = bandBase;
        m_formBandCount = bandCount;
        m_formBandRowsOwed = (bandCount > 0);
        // A field that moved this pack owes its previous values again at the next, when this pack's become them.
        m_frameSampledMoving = moving;
        if (moved > 0) { NoteDisplacedRows(); }
    }
    // Copies the frame's live form bands into the scratch in the order of their first slots, the order the kernels search
    // them in (sdfFormBandOf), at most SdfFormBand.MaxBands of them, and returns how many.
    private int SortFormBands(IReadOnlyList<SdfFormBand> bands) {
        if (m_formBandScratch.Length < Math.Min(val1: bands.Count, val2: SdfFormBand.MaxBands)) {
            m_formBandScratch = new SdfFormBand[Math.Min(val1: bands.Count, val2: SdfFormBand.MaxBands)];
        }

        var count = 0;

        for (var index = 0; ((index < bands.Count) && (count < SdfFormBand.MaxBands)); index++) {
            if (bands[index].IsLive) { m_formBandScratch[count++] = bands[index]; }
        }
        m_formBandScratch.AsSpan(start: 0, length: count).Sort(comparison: static (left, right) => left.FirstSlot.CompareTo(value: right.FirstSlot));
        return count;
    }
    // Writes the sorted form bands' words (SdfFrameSampled's band layout), each band's first mote counted among those
    // before it and its count cut to what SdfFormBand.MaxMotes leaves; their draw rows wait for the frame's staged draws
    // (PackFormBandRows). Returns the motes the bands generate together.
    private uint PackFormBands(Span<uint> words, int count) {
        var motes = 0;

        for (var index = 0; (index < count); index++) {
            ref readonly var band = ref m_formBandScratch[index];
            var record = words.Slice(start: (index * SdfFrameSampled.BandWords), length: SdfFrameSampled.BandWords);
            var moteCount = Math.Clamp(value: band.MoteCount, min: 0, max: (SdfFormBand.MaxMotes - motes));
            var axis = Vector3.Normalize(value: band.Axis);

            record.Clear();
            record[SdfFrameSampled.BandFirstSlotWord] = (uint)band.FirstSlot;
            record[SdfFrameSampled.BandSlotCountWord] = (uint)band.SlotCount;
            record[SdfFrameSampled.BandFlagsWord] = (band.Side switch {
                SdfFormBandSide.Swept => SdfFrameSampled.BandSweptFlag,
                SdfFormBandSide.Whole => SdfFrameSampled.BandWholeFlag,
                _ => 0u,
            } | (band.Parted ? SdfFrameSampled.BandPartedFlag : 0u));
            record[SdfFrameSampled.BandSeedWord] = band.Seed;
            SdfProgram.WriteVector4(words: record, baseIndex: SdfFrameSampled.BandOriginWord, x: band.Origin.X, y: band.Origin.Y, z: band.Origin.Z, w: band.Low);
            SdfProgram.WriteVector4(words: record, baseIndex: SdfFrameSampled.BandAxisWord, x: axis.X, y: axis.Y, z: axis.Z, w: Math.Max(val1: band.High, val2: band.Low));
            SdfProgram.WriteVector4(words: record, baseIndex: SdfFrameSampled.BandEdgeWord, x: band.EdgeLight.X, y: band.EdgeLight.Y, z: band.EdgeLight.Z, w: Math.Max(val1: band.EdgeWidth, val2: 0f));
            SdfProgram.WriteVector4(words: record, baseIndex: SdfFrameSampled.BandSweepWord, x: Math.Max(val1: band.Roughness, val2: 0f), y: Math.Max(val1: band.NoiseScale, val2: 0f), z: Math.Max(val1: band.Elapsed, val2: 0f), w: band.Seconds);
            SdfProgram.WriteVector4(words: record, baseIndex: SdfFrameSampled.BandMoteLightWord, x: band.MoteLight.X, y: band.MoteLight.Y, z: band.MoteLight.Z, w: Math.Max(val1: band.MoteSize, val2: 0f));
            record[SdfFrameSampled.BandMoteWord] = (uint)moteCount;
            record[(SdfFrameSampled.BandMoteWord + 1)] = (uint)motes;
            record[(SdfFrameSampled.BandMoteWord + 2)] = BitConverter.SingleToUInt32Bits(value: Math.Max(val1: band.MoteSeconds, val2: 1e-3f));
            record[(SdfFrameSampled.BandMoteWord + 3)] = BitConverter.SingleToUInt32Bits(value: band.MoteSpeed);
            record[SdfFrameSampled.BandMotionWord] = BitConverter.SingleToUInt32Bits(value: band.MoteRise);
            record[(SdfFrameSampled.BandMotionWord + 1)] = BitConverter.SingleToUInt32Bits(value: band.MoteSwirl);
            motes += moteCount;
        }
        return (uint)motes;
    }
    // Names, for each form band the latest pack wrote, the staged draw rows riding its slots that its motes take surface
    // points from: up to SdfFrameSampled.BandRowCapacity of them, a group's drawn tier before its kept floor. Runs after
    // the frame's draws are staged, so a row named is the row the kernels read this frame (sdfMeshRecord).
    private void PackFormBandRows() {
        if (!m_formBandRowsOwed) { return; }
        m_formBandRowsOwed = false;

        var count = m_formBandCount;
        var capacity = SdfFrameSampled.BandRowCapacity;

        if (m_formBandRows.Length < (count * capacity)) { m_formBandRows = new uint[(count * capacity)]; }
        if (m_formBandRowCounts.Length < count) { m_formBandRowCounts = new int[count]; }
        Array.Clear(array: m_formBandRowCounts, index: 0, length: count);
        for (var pass = 0; (pass < 2); pass++) {
            for (var row = 0; (row < m_stagedCount); row++) {
                ref readonly var draw = ref m_stagedDraws[row];

                if ((draw.FrameSlot is not { } slot) || (draw.Mesh is null) || (draw.Band.IsFloor != (pass == 1))) { continue; }

                var band = FormBandOf(slot: slot, count: count);

                if ((band < 0) || (m_formBandRowCounts[band] >= capacity)) { continue; }
                m_formBandRows[((band * capacity) + m_formBandRowCounts[band]++)] = (uint)row;
            }
        }

        Span<uint> words = stackalloc uint[(1 + SdfFrameSampled.BandRowCapacity)];

        for (var band = 0; (band < count); band++) {
            words.Clear();
            words[0] = (uint)m_formBandRowCounts[band];
            m_formBandRows.AsSpan(start: (band * capacity), length: m_formBandRowCounts[band]).CopyTo(destination: words[1..]);
            if (m_frameSampledRegion.Write(bytes: MemoryMarshal.AsBytes(span: words[..1]), offset: (sizeof(uint) * ((m_formBandBase + (band * SdfFrameSampled.BandWords)) + SdfFrameSampled.BandMotionWord + 2))) |
                m_frameSampledRegion.Write(bytes: MemoryMarshal.AsBytes(span: words[1..]), offset: (sizeof(uint) * ((m_formBandBase + (band * SdfFrameSampled.BandWords)) + SdfFrameSampled.BandRowsWord)))) {
                m_frameSampledRevision++;
            }
        }
    }
    // The sorted band whose run holds a slot, or -1.
    private int FormBandOf(int slot, int count) {
        var low = 0;
        var high = (count - 1);

        while (low <= high) {
            var middle = ((low + high) >> 1);
            ref readonly var band = ref m_formBandScratch[middle];

            if (slot < band.FirstSlot) { high = (middle - 1); } else if (slot >= (band.FirstSlot + band.SlotCount)) { low = (middle + 1); } else { return middle; }
        }
        return -1;
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
