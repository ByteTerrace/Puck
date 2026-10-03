namespace Puck.Abstractions.Gpu;

public sealed partial class GpuKernelCounters {
    /// <summary>Gets a frame slot's physical row capacity.</summary>
    /// <param name="slot">The frame slot.</param>
    /// <returns>The rows in each of its buffers.</returns>
    public int RowsOf(int slot) => m_rows[slot];
    /// <summary>Grows a waited and read frame slot. A smaller request retains its buffers. Creation failure leaves
    /// the old pair intact; the caller must complete the previous submission before calling.</summary>
    /// <param name="slot">The completed frame slot.</param>
    /// <param name="rows">The required pass and detail rows, at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException">Rows is less than one.</exception>
    /// <exception cref="ObjectDisposedException">The counters are disposed.</exception>
    public void EnsureRows(int slot, int rows) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rows, other: 1);
        if (rows <= m_rows[slot]) { return; }
        var size = checked((((ulong)rows) * ((ulong)RowBytes)));
        IGpuBuffer? counter = null;
        IGpuReadbackBuffer? readback = null;

        try {
            counter = m_buffers.CreateDeviceLocal(sizeBytes: size, usage: GpuBufferUsage.Storage,
                name: new GpuObjectName(owner: m_owner, part: m_part, index: slot));
            readback = m_buffers.CreateReadback(sizeBytes: size,
                name: new GpuObjectName(owner: m_owner, part: m_part, index: slot, detail: ReadbackDetail));
            if (m_read.Length < checked((int)size)) { m_read = new byte[checked((int)size)]; }
        } catch { readback?.Dispose(); counter?.Dispose(); throw; }
        m_counters[slot].Dispose();
        m_readbacks[slot].Dispose();
        m_counters[slot] = counter;
        m_readbacks[slot] = readback;
        m_rows[slot] = rows;
        Rows = Math.Max(val1: Rows, val2: rows);
    }
}
