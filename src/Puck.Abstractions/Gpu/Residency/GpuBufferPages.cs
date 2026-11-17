namespace Puck.Abstractions.Gpu;

/// <summary>A buffer whose destination owns page admission. Mapping implementations publish writable pages only
/// after ordering their mapping before writes; they revoke pages before recording retirement or reuse.</summary>
public interface IGpuPagedBuffer : IGpuBuffer {
    /// <summary>Gets this destination's page authority, retained for its entire lifetime.</summary>
    GpuBufferPages Pages { get; }
}

/// <summary>A destination's writable coverage and page generations. All access is on its submission thread.
/// Revocation invalidates existing leases immediately; restoring coverage never restores an old generation.
/// This is a write/publication proof, not a GPU completion proof: physical reuse still requires the device timeline.</summary>
public sealed class GpuBufferPages {
    /// <summary>The smallest supported mapping page, in bytes.</summary>
    public const int MinimumPageBytes = 4096;
    private ulong[] m_generations;
    private bool[] m_writable;
    private ulong m_generation = 1;
    private long m_bytes;

    /// <summary>Creates a destination's page authority.</summary>
    /// <param name="bytes">The address extent, in whole words.</param>
    /// <param name="pageBytes">The native page size, a whole number of words at least <see cref="MinimumPageBytes"/>.</param>
    /// <param name="writable">Whether the initial extent already has ordered backing.</param>
    public GpuBufferPages(long bytes, int pageBytes = 65536, bool writable = true) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageBytes, MinimumPageBytes);
        if ((bytes % 4) != 0 || (pageBytes % 4) != 0) { throw new ArgumentException("Page extents use whole words."); }
        PageBytes = pageBytes;
        m_bytes = bytes;
        m_generations = new ulong[checked((int)((bytes + pageBytes - 1) / pageBytes))];
        m_writable = new bool[m_generations.Length];
        Array.Fill(m_generations, m_generation);
        Array.Fill(m_writable, writable);
    }

    /// <summary>Gets the mapping page size in bytes.</summary>
    public int PageBytes { get; }

    /// <summary>Gets the writable-coverage revision. Consumers revisit retained leases only when it changes.</summary>
    public ulong Revision => m_generation;

    // The host shadow remains authoritative after remapping. Source-owned ranges are re-owed by their
    // publication owner, which knows which meshes remain live.
    internal void OweChanged(ulong after, long offset, long bytes, GpuUploadRuns owed) {
        if (after == m_generation || bytes == 0) { return; }
        var end = offset + bytes;
        var last = (int)((end - 1) / PageBytes);
        for (var page = (int)(offset / PageBytes); page <= last; page++) {
            if (m_generations[page] <= after) { continue; }
            var first = Math.Max(offset, (long)page * PageBytes);
            var stop = Math.Min(end, (long)(page + 1) * PageBytes);
            owed.Add((int)((first - offset) / 4), (int)((stop - first) / 4));
        }
    }

    /// <summary>Issues a lease only when the entire range has writable backing.</summary>
    /// <param name="offset">The first byte.</param>
    /// <param name="bytes">The positive byte count.</param>
    /// <returns>The destination-issued proof, or null for an uncovered range.</returns>
    public Lease? TryLease(long offset, long bytes) {
        if (offset < 0 || bytes <= 0 || offset > m_bytes || bytes > m_bytes - offset) { return null; }
        var first = (int)(offset / PageBytes);
        var last = (int)((offset + bytes - 1) / PageBytes);
        var generation = m_generation;
        for (var page = first; page <= last; page++) { if (!m_writable[page]) { return null; } }
        return new Lease(this, offset, bytes, generation);
    }

    /// <summary>Publishes or revokes whole pages. Every transition advances their generation before returning.</summary>
    /// <param name="firstPage">The first page.</param>
    /// <param name="count">The number of pages.</param>
    /// <param name="writable">True only after native mapping has been ordered before destination writes.</param>
    public void SetWritable(int firstPage, int count, bool writable) {
        ArgumentOutOfRangeException.ThrowIfNegative(firstPage);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstPage, m_writable.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, m_writable.Length - firstPage);
        var generation = checked(++m_generation);
        for (var page = firstPage; page < firstPage + count; page++) {
            m_writable[page] = writable;
            m_generations[page] = generation;
        }
    }

    // Contiguous replacement preserves old words through an ordered carry; only its newly backed tail changes.
    internal void GrowCarried(long bytes) {
        var old = m_writable.Length;
        var count = Math.Max(old, checked((int)((bytes + PageBytes - 1) / PageBytes)));
        Array.Resize(ref m_writable, count);
        Array.Resize(ref m_generations, count);
        Array.Fill(m_writable, true, old, count - old);
        Array.Fill(m_generations, m_generation, old, count - old);
        m_bytes = Math.Max(m_bytes, bytes);
    }

    internal void Revoke() => SetWritable(0, m_writable.Length, false);

    // Exact intersection after GpuUploadRuns coarsening. Never feed these runs back through its gap joining.
    internal void Clip(int start, int length, int destinationWord, List<(int Start, int Length, Lease Lease)> output, GpuUploadRuns deferred) {
        var end = checked(start + length);
        while (start < end) {
            var offset = ((long)destinationWord + start) * 4;
            var page = (int)(offset / PageBytes);
            var next = (int)Math.Min(end, (((long)(page + 1) * PageBytes) / 4) - destinationWord);
            if (page < m_writable.Length && m_writable[page]) {
                var first = start;
                while (next < end && page + 1 < m_writable.Length && m_writable[page + 1]) {
                    page++;
                    next = (int)Math.Min(end, (((long)(page + 1) * PageBytes) / 4) - destinationWord);
                }
                var lease = TryLease(((long)destinationWord + first) * 4, ((long)next - first) * 4);
                if (lease is { } issued) { output.Add((first, next - first, issued)); }
            } else {
                deferred.Add(start, next - start);
            }
            start = next;
        }
    }

    /// <summary>A destination-issued write proof. A generation change on any covered page invalidates it forever.</summary>
    public readonly struct Lease {
        private readonly GpuBufferPages? m_owner;
        private readonly long m_offset;
        private readonly long m_bytes;
        private readonly ulong m_generation;
        internal Lease(GpuBufferPages owner, long offset, long bytes, ulong generation) {
            m_owner = owner; m_offset = offset; m_bytes = bytes; m_generation = generation;
        }
        /// <summary>Gets whether every page still has the backing generation present when the write was recorded.</summary>
        public bool IsCurrent {
            get {
                if (m_owner is null || m_offset + m_bytes > m_owner.m_bytes) { return false; }
                if (m_owner.m_generation == m_generation) { return true; }
                var last = (int)((m_offset + m_bytes - 1) / m_owner.PageBytes);
                for (var page = (int)(m_offset / m_owner.PageBytes); page <= last; page++) {
                    if (!m_owner.m_writable[page] || m_owner.m_generations[page] > m_generation) { return false; }
                }
                return true;
            }
        }
    }
}
