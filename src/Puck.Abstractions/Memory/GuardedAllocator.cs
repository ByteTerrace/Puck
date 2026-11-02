using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Abstractions.Memory;

/// <summary>
/// A diagnostic decorator over the host allocator that catches native heap misuse where it happens instead of where the
/// damaged heap later faults: a free of a block it never handed out (or whose header was overwritten), a second free of
/// a block, a write past a block's end, and a write into a block after its free. Each block carries a header and a
/// trailing canary; a freed block is poisoned and held in a bounded quarantine with the stack that freed it, and is
/// checked for writes when it leaves the quarantine, on <see cref="Collect"/> and on <see cref="Verify"/>. A finding
/// ends the process with <see cref="Environment.FailFast(string)"/>, naming the block and the stack that freed it. The
/// World's <c>--debug-allocator</c> installs it and verifies the quarantine at exit; it costs a stack walk a free and
/// the quarantine's bytes, so it is a reproduction tool, never the default.
/// </summary>
/// <param name="inner">The allocator that provides and finally frees the storage.</param>
/// <param name="quarantineBytes">The most bytes of freed blocks, their headers and canaries included, held for the
/// write-after-free check before the oldest is released.</param>
public sealed unsafe class GuardedAllocator(IAllocator inner, long quarantineBytes = (1L << 30)) : IAllocator, IAllocatorStatistics {
    private const ulong LiveMagic = 0x4B43_4F4C_4244_5547UL;
    private const ulong FreedMagic = 0x4545_5246_4244_5547UL;
    private const byte Poison = 0xDF;
    private const byte Canary = 0xFD;
    private const int TailBytes = 16;
    private const nuint MinimumAlignment = 16;

    // Sits immediately before the block handed out, whatever the alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct Header {
        public ulong Magic;
        public nuint Size;
        public nuint Offset;
        public long Sequence;
    }

    private readonly Lock m_gate = new();
    private readonly Queue<nint> m_quarantine = new();
    private readonly Dictionary<nint, string> m_freedBy = [];

    private long m_quarantined;
    private long m_sequence;

    /// <inheritdoc/>
    /// <remarks>A request whose guarded layout cannot be represented (an alignment that is not a power of two, or a
    /// size whose header and canary overflow the address space) fails with <see langword="null"/>.</remarks>
    public void* Allocate(nuint size, nuint alignment = 0) {
        if (!TryLayout(size: size, alignment: alignment, align: out var align, offset: out var offset, total: out var total)) { return null; }
        var block = ((byte*)inner.Allocate(alignment: align, size: total));

        if (block is null) { return null; }
        var user = (block + offset);
        var header = (((Header*)user) - 1);

        header->Magic = LiveMagic;
        header->Size = size;
        header->Offset = offset;
        header->Sequence = Interlocked.Increment(location: ref m_sequence);
        new Span<byte>(pointer: (user + size), length: TailBytes).Fill(value: Canary);
        return user;
    }
    /// <inheritdoc/>
    public void Free(void* ptr) {
        if (ptr is null) { return; }
        var user = ((byte*)ptr);
        var header = (((Header*)user) - 1);
        // One free wins the block; any other free of it, concurrent or later, sees it freed.
        var magic = Interlocked.CompareExchange(comparand: LiveMagic, location1: ref header->Magic, value: FreedMagic);

        if (magic == FreedMagic) {
            string? earlier;

            lock (m_gate) { _ = m_freedBy.TryGetValue(key: ((nint)user), value: out earlier); }
            Fail(message: $"double free of a {header->Size}-byte native block (#{header->Sequence}) at 0x{((nint)user):x}", earlier: earlier);
        }
        if (magic != LiveMagic) {
            Fail(message: $"free of 0x{((nint)user):x}, which this allocator never handed out or whose header was overwritten (magic 0x{magic:x})", earlier: null);
        }
        var size = header->Size;
        var tail = new ReadOnlySpan<byte>(pointer: (user + size), length: TailBytes);

        if (tail.IndexOfAnyExcept(value: Canary) is var past and >= 0) {
            Fail(message: $"write past the end of a {size}-byte native block (#{header->Sequence}) at 0x{((nint)user):x}, {past} bytes beyond it", earlier: null);
        }
        Fill(user: user, size: size);

        var freedBy = new StackTrace(fNeedFileInfo: false, skipFrames: 1).ToString();

        lock (m_gate) {
            m_quarantine.Enqueue(item: ((nint)user));
            m_freedBy[((nint)user)] = freedBy;
            m_quarantined += HeldBytes(header: header);
            while ((m_quarantined > quarantineBytes) && m_quarantine.TryDequeue(result: out var oldest)) { Release(user: ((byte*)oldest)); }
        }
    }
    /// <inheritdoc/>
    public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) {
        if (ptr is null) { return Allocate(alignment: alignment, size: newSize); }
        var header = (((Header*)ptr) - 1);

        if (Volatile.Read(location: ref header->Magic) != LiveMagic) {
            Fail(message: $"reallocation of 0x{((nint)ptr):x}, which is freed or was never handed out (magic 0x{header->Magic:x})", earlier: null);
        }
        var replacement = Allocate(alignment: alignment, size: newSize);

        if (replacement is null) { return null; }
        Buffer.MemoryCopy(destinationSizeInBytes: newSize, source: ptr, destination: replacement, sourceBytesToCopy: Math.Min(val1: header->Size, val2: newSize));
        Free(ptr: ptr);
        return replacement;
    }
    /// <inheritdoc/>
    /// <remarks>Reads the inner allocator's statistics, quarantine and guard bytes included; zero when it keeps none.</remarks>
    public (long Committed, long Reserved) ReadStatistics() => ((inner as IAllocatorStatistics)?.ReadStatistics() ?? default);
    /// <inheritdoc/>
    /// <remarks>Checks every quarantined block first (<see cref="Verify"/>).</remarks>
    public void Collect() {
        _ = Verify();
        (inner as IAllocatorStatistics)?.Collect();
    }
    /// <summary>Checks every quarantined block for a write after its free, ending the process on the first found.</summary>
    /// <returns>The number of quarantined blocks checked.</returns>
    public int Verify() {
        lock (m_gate) {
            foreach (var user in m_quarantine) { Check(user: ((byte*)user)); }
            return m_quarantine.Count;
        }
    }

    // The inner request for a block: an alignment of at least MinimumAlignment, the header rounded up to it, the block
    // and its canary; false when the alignment is not a power of two or the total does not fit the address space.
    private static bool TryLayout(nuint size, nuint alignment, out nuint align, out nuint offset, out nuint total) {
        align = ((alignment < MinimumAlignment) ? MinimumAlignment : alignment);
        offset = total = 0;
        if ((align & (align - 1)) != 0) { return false; }
        offset = ((align >= (nuint)sizeof(Header)) ? align : (nuint)sizeof(Header));
        if (size > ((nuint.MaxValue - offset) - TailBytes)) { return false; }
        total = ((offset + size) + TailBytes);
        return true;
    }
    // The bytes a quarantined block holds of the inner allocator: its alignment prefix and header, the block and its
    // canary.
    private static long HeldBytes(Header* header) => checked((long)((header->Offset + header->Size) + TailBytes));
    // Under the gate: checks the oldest quarantined block and gives it back to the inner allocator.
    private void Release(byte* user) {
        var header = (((Header*)user) - 1);

        Check(user: user);
        m_quarantined -= HeldBytes(header: header);
        _ = m_freedBy.Remove(key: ((nint)user));
        header->Magic = 0;
        inner.Free(ptr: (user - header->Offset));
    }
    // Under the gate.
    private void Check(byte* user) {
        var header = (((Header*)user) - 1);
        var size = header->Size;

        if (header->Magic != FreedMagic) {
            Fail(message: $"the header of a quarantined native block at 0x{((nint)user):x} was overwritten after its free (magic 0x{header->Magic:x})", earlier: m_freedBy.GetValueOrDefault(key: ((nint)user)));
        }
        for (nuint start = 0; (start < size);) {
            var length = ((int)Math.Min(val1: (size - start), val2: ((nuint)int.MaxValue)));
            var found = new ReadOnlySpan<byte>(pointer: (user + start), length: length).IndexOfAnyExcept(value: Poison);

            if (found >= 0) {
                Fail(message: $"write after free into a {size}-byte native block (#{header->Sequence}) at 0x{((nint)user):x}, offset {(start + (nuint)found)}", earlier: m_freedBy.GetValueOrDefault(key: ((nint)user)));
            }
            start += (nuint)length;
        }
    }
    private static void Fill(byte* user, nuint size) {
        for (nuint start = 0; (start < size);) {
            var length = ((int)Math.Min(val1: (size - start), val2: ((nuint)int.MaxValue)));

            new Span<byte>(pointer: (user + start), length: length).Fill(value: Poison);
            start += (nuint)length;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fail(string message, string? earlier) =>
        Environment.FailFast(message: $"[memory.guard] {message}{((earlier is null) ? string.Empty : $"; it was freed at:{Environment.NewLine}{earlier}")}");
}
