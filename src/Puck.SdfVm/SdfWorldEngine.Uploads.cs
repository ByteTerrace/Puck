using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

// Host-visible upload bookkeeping. The viewport rows, dynamic transforms and frame instance grid live in persistent
// device-local tables; each has a host mirror that always equals what the device table holds once the owed word
// ranges below are copied. A frame packs its inputs against the mirrors, owes only the ranges whose bytes changed,
// stages those ranges in the ring slot's host-visible buffer, and records one copy dispatch per table that owes any.
//
// A slot's staging buffer is [run table: FrameUploadRunTableWords][table], each range staged at FrameUploadRunTableWords
// + its own table offset. One owed range rides the push constants alone; two or more also stage the run table: per run,
// (table offset, prefix) in uints, where prefix is the run's first thread index — the sum of the lengths before it —
// so each thread of the one dispatch finds its run by binary search (Puck.Shaders' region-copy.comp.hlsl).
//
// The staging buffers are never read outside what the same frame wrote, so a slot's stale remainder is harmless: the
// device-local table is the single copy the kernels read, and the top-of-frame barrier orders every frame's copies
// after the previous frame's reads and before this frame's.
public sealed partial class SdfWorldEngine {
    private const int DynamicTransformTable = 1;
    private const int DynamicTransformWordCount = (DynamicTransformByteLength / sizeof(uint));
    // The staging buffer's leading run-table reserve, in uints: two per run.
    private const int FrameUploadRunTableWords = (MaxUploadRunsPerTable * 2);
    // Instance-grid words a changed range still joins the previous one across: a run-table entry costs two words, so
    // copying a gap of up to two unchanged words costs no more than describing another run.
    private const int InstanceGridMergeGapWords = 2;
    private const int InstanceGridTable = 2;

    // The most runs one table's copy carries per frame; past it neighbouring runs pair up, re-sending the gap between
    // them. A tunable: it bounds the run-table reserve and the copy's binary search, never the dispatch count.
    internal const int MaxUploadRunsPerTable = GpuRegion.MaxCopyRuns;

    // The most separate owed ranges one ring-table slot holds before neighbours pair up.
    private const int RingTableRunCapacity = 8;
    private const int ViewportTable = 0;
    private const int ViewportWordCount = (ViewportByteLength / sizeof(uint));

    /// <summary>The most words one device-local table may hold: its whole-table first copy is one one-dimensional
    /// dispatch of <c>region-copy.comp</c>, 64 threads per group, and one dispatch dimension carries at most
    /// 65,535 groups on both backends (Vulkan's guaranteed <c>maxComputeWorkGroupCount</c>, Direct3D 12's
    /// <c>D3D12_CS_DISPATCH_MAX_THREAD_GROUPS_PER_DIMENSION</c>).</summary>
    public const ulong MaxFrameUploadTableWords = (65_535UL * GpuRegion.CopyWorkgroupSize);

    /// <summary>Refuses, by table name, a device-local table too large for one copy dispatch, so an oversized table
    /// fails where it is sized instead of dispatching past the group limit.</summary>
    /// <param name="table">The table's name, as the refusal prints it.</param>
    /// <param name="byteLength">The table's size in bytes.</param>
    /// <exception cref="InvalidOperationException">The table holds more than <see cref="MaxFrameUploadTableWords"/>
    /// words.</exception>
    public static void RequireOneCopyDispatch(string table, ulong byteLength) {
        var words = (byteLength / sizeof(uint));

        if (words > MaxFrameUploadTableWords) {
            throw new InvalidOperationException(message: $"the SDF engine's {table} table holds {words} words, past the {MaxFrameUploadTableWords} one copy dispatch carries (65535 groups of {GpuRegion.CopyWorkgroupSize} threads); lower the capacity that sizes it.");
        }
    }

    // The owed word ranges per device-local table, in FrameUploadTableCount order. PrepareFrame stages them into the
    // ring slot's buffer and RecordFrameUpload copies and then clears them, so a frame that fails before recording
    // stages them again on the next one.
    private readonly GpuUploadRuns[] m_tableUploads = [
        new(capacity: MaxUploadRunsPerTable),
        new(capacity: MaxUploadRunsPerTable),
        new(
            capacity: MaxUploadRunsPerTable,
            mergeGap: InstanceGridMergeGapWords
        ),
    ];
    // The run table WriteOwedWords stages ahead of a table's ranges.
    private readonly uint[] m_runTableScratch = new uint[FrameUploadRunTableWords];

    // The host mirror of the device-local instance grid (m_viewportScratch and m_dynamicTransformScratch mirror theirs).
    private uint[] m_instanceGridMirror;
    // How far each device-local table is known to match its mirror, in rows, slots and words. Past it the device
    // contents are undefined, so anything packed there is owed whatever the mirror already holds.
    private int m_dynamicTransformSlotsResident;
    private int m_instanceGridWordsResident;
    private int m_viewportRowsResident;
    // One more whenever a packed dynamic-transform slot's bytes change: the cadence signature folds this in place of
    // hashing the whole table.
    private ulong m_dynamicTransformRevision;

    // The moved set this engine last consumed transforms from, the serial of that frame, and the scratch the rows owed
    // since then are unioned into.
    private readonly GpuUploadRuns m_owedTransforms = new(capacity: MaxUploadRunsPerTable);

    private SdfMovedTransforms? m_movedTransformsSource;
    private long m_movedTransformsSerial;
    private IReadOnlyList<DynamicTransform>? m_movedTransformsTable;
    // A per-frame grid rebuild owed whether or not a transform moved: set by UploadProgram for a moving-instance program.
    private bool m_instanceGridRebuildOwed;
    // Whether the program buffer holds m_liveProgram's words: false before the first upload and after the buffer grows.
    private bool m_programBufferCurrent;

    // The size of a table's ring-slot staging buffer: the run-table reserve, then the table.
    private static ulong FrameUploadStagingBytes(int tableBytes) =>
        ((((ulong)FrameUploadRunTableWords) * sizeof(uint)) + ((ulong)tableBytes));
    // Owes every word of the device-local instance grid again: its buffer was replaced, so its contents are undefined.
    private void ForgetInstanceGridResidency(int wordCapacity) {
        m_instanceGridMirror = new uint[wordCapacity];
        m_instanceGridWordsResident = 0;
        m_tableUploads[InstanceGridTable].Clear();
    }
    // Owes a device-local table whole on its first frame, so every row or slot a kernel could index is defined from
    // then on, packed or not; returns the resident extent the frame's packing compares within.
    private int OweWholeTableOnce(int table, int resident, int entries, int entryWords) {
        if (resident > 0) {
            return resident;
        }

        m_tableUploads[table].Add(
            length: (entries * entryWords),
            start: 0
        );

        return entries;
    }
    // Copies the packed row or slot into the mirror when it differs (or lies past the resident extent) and owes it.
    private bool StageTableEntry(int table, int entry, int resident, ReadOnlySpan<float> packed, Span<float> mirror) {
        var start = (entry * packed.Length);
        var destination = mirror.Slice(
            length: packed.Length,
            start: start
        );

        if (
            (entry < resident) &&
            MemoryMarshal.AsBytes(span: packed).SequenceEqual(other: MemoryMarshal.AsBytes(span: destination))
        ) {
            return false;
        }

        packed.CopyTo(destination: destination);
        m_tableUploads[table].Add(
            length: packed.Length,
            start: start
        );

        return true;
    }
    // Copies a packed row the caller already knows is owed into the mirror and owes it.
    private void StageTableRow(int table, int entry, ReadOnlySpan<float> packed, Span<float> mirror) {
        var start = (entry * packed.Length);

        packed.CopyTo(destination: mirror.Slice(
            length: packed.Length,
            start: start
        ));
        m_tableUploads[table].Add(
            length: packed.Length,
            start: start
        );
    }
    // Diffs a freshly built instance grid against the mirror, owes the changed word ranges, and adopts it.
    private void StageInstanceGrid(ReadOnlySpan<uint> words) {
        var owed = m_tableUploads[InstanceGridTable];
        var mirror = m_instanceGridMirror.AsSpan(
            length: words.Length,
            start: 0
        );
        var resident = Math.Min(
            val1: m_instanceGridWordsResident,
            val2: words.Length
        );
        var index = 0;

        while (index < resident) {
            index += words[index..resident].CommonPrefixLength(other: mirror[index..resident]);

            if (index == resident) {
                break;
            }

            var start = index;

            do {
                index++;
            } while (
                (index < resident) &&
                (words[index] != mirror[index])
            );

            owed.Add(
                length: (index - start),
                start: start
            );
        }

        owed.Add(
            length: (words.Length - resident),
            start: resident
        );
        words.CopyTo(destination: mirror);
        m_instanceGridWordsResident = Math.Max(
            val1: m_instanceGridWordsResident,
            val2: words.Length
        );
    }
    // Stages each table's owed ranges from its mirror into this ring slot's host-visible buffer.
    private void WriteStagedUploads(int slot) {
        WriteOwedWords(
            buffer: m_viewportBuffers[slot],
            mirror: MemoryMarshal.Cast<byte, uint>(span: m_viewportScratch.AsSpan()),
            owed: m_tableUploads[ViewportTable]
        );
        WriteOwedWords(
            buffer: m_dynamicTransformBuffers[slot],
            mirror: MemoryMarshal.Cast<byte, uint>(span: m_dynamicTransformScratch.AsSpan()),
            owed: m_tableUploads[DynamicTransformTable]
        );
        WriteOwedWords(
            buffer: m_instanceGridBuffers[slot],
            mirror: m_instanceGridMirror,
            owed: m_tableUploads[InstanceGridTable]
        );
    }
    // Writes only the span of words that differs from the words the program buffer already holds: from the first
    // differing word to the last, or to the end when the new program is longer.
    private void WriteProgramWords(SdfProgram program) {
        var words = program.Words;
        var resident = (m_programBufferCurrent
            ? m_liveProgram.Words
            : []
        );
        var shared = Math.Min(
            val1: words.Length,
            val2: resident.Length
        );
        var first = words[..shared].CommonPrefixLength(other: resident[..shared]);

        m_programBufferCurrent = true;

        if (first == words.Length) {
            return;
        }

        var last = (words.Length - 1);

        if (words.Length <= resident.Length) {
            while (words[last] == resident[last]) {
                last--;
            }
        }

        m_programBuffer.Write<uint>(
            data: words[first..(last + 1)],
            destinationOffsetBytes: (((ulong)first) * sizeof(uint))
        );
    }
    // Stages one table's owed ranges after the run-table reserve, and the run table itself when there are two or more.
    private void WriteOwedWords(GpuUploadRuns owed, IGpuStorageBuffer buffer, ReadOnlySpan<uint> mirror) {
        var prefix = 0;

        for (var run = 0; (run < owed.Count); run++) {
            var start = owed.Start(index: run);
            var length = owed.Length(index: run);

            buffer.Write<uint>(
                data: mirror.Slice(
                    length: length,
                    start: start
                ),
                destinationOffsetBytes: (((ulong)(FrameUploadRunTableWords + start)) * sizeof(uint))
            );
            m_runTableScratch[(run * 2)] = ((uint)start);
            m_runTableScratch[((run * 2) + 1)] = ((uint)prefix);
            prefix += length;
        }

        if (owed.Count > 1) {
            buffer.Write<uint>(data: m_runTableScratch.AsSpan(
                length: (owed.Count * 2),
                start: 0
            ));
        }
    }
}
