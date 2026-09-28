using System.Buffers.Binary;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The counter buffers a node's kernels count their own work into (<see cref="GpuWork.KernelKinds"/>), and the readback
/// its ledger reads them from once a submission completes. Each frame slot has a device-local storage buffer of
/// <see cref="Rows"/> rows and a readback buffer of the same size. A row is one pass's counts, the pass's index in the
/// node's configured passes, and holds each kernel kind in <see cref="GpuWork.KernelKinds"/> order as one 64-bit count
/// in two 32-bit words, low word first, which a kernel adds to with one atomic on the low word and one on the high word
/// when that addition carries. The HLSL that adds to a row is generated from these constants into every pass interface
/// that declares the work counters (<c>ShaderWorkCounters</c> in <c>Puck.Shaders</c>).
/// <para>
/// Every frame records <see cref="RecordClear"/> before its first pass and <see cref="RecordCopy"/> after its last,
/// outside every pass: one clear, one copy and a buffer barrier after each clear and before each copy. The passes between
/// add atomically and need no barrier between them. The frame's submission names the slot to its ledger
/// (<see cref="GpuWorkLedger.ReadOnCompletion"/>), which reads it once the submission has completed and before the slot
/// is recorded again, so a slot's readback is never read while a copy into it is in flight.
/// </para>
/// </summary>
public sealed class GpuKernelCounters : IGpuWorkReadback, IDisposable {
    /// <summary>The 32-bit words one count takes: its low word, then its high word.</summary>
    public const int CountWords = 2;

    // The sample column of each kernel kind, in GpuWork.KernelKinds order.
    private static readonly int[] KernelColumns = [GpuWork.MarchStepsColumn, GpuWork.TexelsWrittenColumn];

    private readonly IGpuBuffer[] m_counters;
    private readonly IGpuReadbackBuffer[] m_readbacks;
    private readonly byte[] m_read;

    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="GpuKernelCounters"/> class, creating each frame slot's
    /// counter and readback buffers through <paramref name="buffers"/>, and releasing what it created when a creation
    /// fails.</summary>
    /// <param name="buffers">The factory the buffers are created through, the node's counting one.</param>
    /// <param name="slots">The node's frame slots, one pair of buffers each; at least one.</param>
    /// <param name="rows">The rows each buffer holds, one per pass the node configures; at least one.</param>
    /// <param name="owner">The owner every buffer is named under (<see cref="GpuObjectName"/>).</param>
    /// <param name="part">The part every buffer is named under; a counter buffer is named at its slot's index, and a
    /// readback buffer with the detail <c>readback</c> too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slots"/> or <paramref name="rows"/> is less than
    /// one.</exception>
    public GpuKernelCounters(IGpuBufferFactory buffers, int slots, int rows, string owner, string part) {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: slots
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: rows
        );

        Rows = rows;
        SizeBytes = ((ulong)(rows * RowBytes));
        m_counters = new IGpuBuffer[slots];
        m_readbacks = new IGpuReadbackBuffer[slots];
        m_read = new byte[((int)SizeBytes)];

        try {
            for (var slot = 0; (slot < slots); slot++) {
                m_counters[slot] = buffers.CreateDeviceLocal(
                    name: new GpuObjectName(
                        index: slot,
                        owner: owner,
                        part: part
                    ),
                    sizeBytes: SizeBytes,
                    usage: GpuBufferUsage.Storage
                );
                m_readbacks[slot] = buffers.CreateReadback(
                    name: new GpuObjectName(
                        detail: ReadbackDetail,
                        index: slot,
                        owner: owner,
                        part: part
                    ),
                    sizeBytes: SizeBytes
                );
            }
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Gets the 32-bit words one row takes: <see cref="CountWords"/> per kernel kind.</summary>
    public static int RowWords =>
        (GpuWork.KernelKinds.Length * CountWords);
    /// <summary>Gets the bytes one row takes.</summary>
    public static int RowBytes =>
        (RowWords * sizeof(uint));
    /// <summary>Gets the rows each buffer holds.</summary>
    public int Rows { get; }
    /// <summary>Gets the size, in bytes, of each counter buffer and of each readback buffer.</summary>
    public ulong SizeBytes { get; }
    /// <summary>Gets the bytes every buffer holds together, device-local and readback: two buffers a frame
    /// slot.</summary>
    public ulong TotalBytes =>
        (((ulong)(m_counters.Length * 2)) * SizeBytes);

    private static string ReadbackDetail => "readback";

    /// <summary>Gets where one pass of a frame slot counts: the slot's counter buffer and the pass's row.</summary>
    /// <param name="slot">The frame slot.</param>
    /// <param name="row">The pass's row, its index in the node's configured passes.</param>
    /// <returns>The slot's device-local counter buffer, which the pass binds and adds to, and its row.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="slot"/> is not a frame slot.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> is not a row of the buffer.</exception>
    public GpuKernelCounterRow RowOf(int slot, int row) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: ((uint)Rows),
            value: ((uint)row),
            paramName: nameof(row)
        );

        return new GpuKernelCounterRow(
            Buffer: m_counters[slot],
            Row: ((uint)row)
        );
    }
    /// <summary>Records the zero clear of a frame slot's counter buffer, then the barrier ordering it before every compute
    /// pass's atomic additions.</summary>
    /// <param name="recorder">The node's counting recorder.</param>
    /// <param name="commandBuffer">The frame's command buffer, ahead of its first pass.</param>
    /// <param name="slot">The frame slot.</param>
    public void RecordClear(IGpuRecorder recorder, nint commandBuffer, int slot) {
        var counter = m_counters[slot].BufferHandle;

        recorder.ClearStorageBuffer(
            bufferHandle: counter,
            commandBufferHandle: commandBuffer,
            sizeBytes: SizeBytes
        );
        recorder.TransitionBuffer(
            bufferHandle: counter,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
    }
    /// <summary>Records the barrier ordering every pass's additions before the copy, then the copy of a frame slot's
    /// counter buffer into its readback.</summary>
    /// <param name="recorder">The node's counting recorder.</param>
    /// <param name="commandBuffer">The frame's command buffer, after its last pass.</param>
    /// <param name="slot">The frame slot.</param>
    public void RecordCopy(IGpuRecorder recorder, nint commandBuffer, int slot) {
        var counter = m_counters[slot].BufferHandle;

        recorder.TransitionBuffer(
            bufferHandle: counter,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.TransferRead,
            destinationStageMask: GpuStage.Transfer,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );
        recorder.CopyBuffer(
            commandBufferHandle: commandBuffer,
            destinationBufferHandle: m_readbacks[slot].BufferHandle,
            sizeBytes: SizeBytes,
            sourceBufferHandle: counter
        );
    }
    /// <inheritdoc/>
    /// <remarks>Reads the slot's readback into a buffer the instance allocated once, so reading allocates
    /// nothing.</remarks>
    public void AddTo(int slot, Span<long> counts, int passCount) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_readbacks[slot].Read(destination: m_read);

        var rows = Math.Min(
            val1: Rows,
            val2: passCount
        );

        for (var row = 0; (row < rows); row++) {
            var rowCounts = counts.Slice(
                length: GpuWork.SubmissionColumnCount,
                start: ((row + 1) * GpuWork.SubmissionColumnCount)
            );

            for (var kind = 0; (kind < KernelColumns.Length); kind++) {
                rowCounts[KernelColumns[kind]] += CountAt(
                    kind: kind,
                    row: row
                );
            }
        }
    }
    /// <summary>Releases every buffer created. Safe to call more than once; the owner calls it once no submission that
    /// recorded into the buffers is in flight.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var counter in m_counters) {
            counter?.Dispose();
        }

        foreach (var readback in m_readbacks) {
            readback?.Dispose();
        }
    }

    // One 64-bit count of a row: its low word, then its high word.
    private long CountAt(int row, int kind) {
        var offset = (((row * RowWords) + (kind * CountWords)) * sizeof(uint));

        return ((long)BinaryPrimitives.ReadUInt64LittleEndian(source: m_read.AsSpan(
            length: sizeof(ulong),
            start: offset
        )));
    }
}
/// <summary>Where one pass counts its kernels' work in a frame (<see cref="GpuKernelCounters.RowOf"/>): the frame slot's
/// counter buffer, which the pass binds read-write, and the row its kernels add to.</summary>
/// <param name="Buffer">The frame slot's device-local counter buffer.</param>
/// <param name="Row">The pass's row, <see cref="GpuKernelCounters.RowWords"/> 32-bit words from the buffer's start per
/// row.</param>
public readonly record struct GpuKernelCounterRow(IGpuBuffer Buffer, uint Row);
