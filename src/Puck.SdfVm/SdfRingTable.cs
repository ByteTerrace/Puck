using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// A host table the kernels read straight from one host-visible buffer per frame-ring slot: the host mirror, plus,
/// for each slot, the byte ranges that slot's buffer has not received since they changed. A change reaches every
/// slot's owed ranges at once, so each slot catches up when its turn comes, and a slot's buffer is written only where
/// it is behind the mirror. Every slot starts owing the whole table, since a new buffer's contents are undefined.
/// Allocation-free after construction.
/// </summary>
internal sealed class SdfRingTable {
    private readonly GpuUploadRuns[] m_owed;

    /// <summary>Initializes a new instance of the <see cref="SdfRingTable"/> class.</summary>
    /// <param name="byteLength">The table's size in bytes.</param>
    /// <param name="slotCount">The number of ring slots, each with its own buffer.</param>
    /// <param name="runCapacity">The most separate owed ranges one slot holds before neighbours pair up.</param>
    public SdfRingTable(int byteLength, int slotCount, int runCapacity) {
        Current = new byte[byteLength];
        m_owed = new GpuUploadRuns[slotCount];

        for (var slot = 0; (slot < slotCount); slot++) {
            m_owed[slot] = new GpuUploadRuns(capacity: runCapacity);
            m_owed[slot].Add(
                length: byteLength,
                start: 0
            );
        }
    }

    /// <summary>Gets the host mirror: the table every slot's buffer converges to. A caller that writes it directly
    /// reports the changed range through <see cref="MarkChanged"/>.</summary>
    public byte[] Current { get; }

    /// <summary>Writes this slot's owed ranges from the mirror into its buffer and clears them.</summary>
    /// <param name="slot">The ring slot whose fence has retired.</param>
    /// <param name="buffer">The slot's host-visible buffer, at least <see cref="Current"/>'s length.</param>
    public void Flush(int slot, IGpuStorageBuffer buffer) {
        var owed = m_owed[slot];

        for (var run = 0; (run < owed.Count); run++) {
            var start = owed.Start(index: run);

            buffer.Write<byte>(
                data: Current.AsSpan(
                    length: owed.Length(index: run),
                    start: start
                ),
                destinationOffsetBytes: ((ulong)start)
            );
        }

        owed.Clear();
    }
    /// <summary>Records that the mirror's bytes <c>[offset, offset + length)</c> changed, so every slot owes them.</summary>
    /// <param name="offset">The first changed byte.</param>
    /// <param name="length">The changed length in bytes.</param>
    public void MarkChanged(int offset, int length) {
        foreach (var owed in m_owed) {
            owed.Add(
                length: length,
                start: offset
            );
        }
    }
    /// <summary>Copies <paramref name="bytes"/> into the mirror at <paramref name="offset"/> and records the span
    /// from the first to the last byte that differed as owed by every slot.</summary>
    /// <param name="offset">The mirror byte offset <paramref name="bytes"/> starts at.</param>
    /// <param name="bytes">The new contents of that span.</param>
    /// <returns><see langword="true"/> when any byte differed.</returns>
    public bool Write(int offset, ReadOnlySpan<byte> bytes) {
        var destination = Current.AsSpan(
            length: bytes.Length,
            start: offset
        );
        var first = bytes.CommonPrefixLength(other: destination);

        if (first == bytes.Length) {
            return false;
        }

        var last = (bytes.Length - 1);

        while (bytes[last] == destination[last]) {
            last--;
        }

        var length = ((last - first) + 1);

        bytes.Slice(
            length: length,
            start: first
        ).CopyTo(destination: destination[first..]);
        MarkChanged(
            length: length,
            offset: (offset + first)
        );

        return true;
    }
}
