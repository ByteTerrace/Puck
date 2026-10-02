namespace Puck.Abstractions.Gpu;

public sealed partial class GpuKernelCounters {
    /// <summary>Gets a slot's actual row capacity.</summary>
    /// <param name="slot">The frame slot.</param>
    /// <returns>The number of rows in its buffer pair.</returns>
    public int RowsOf(int slot) => m_rows[slot];

    /// <summary>Gets retained readback scratch and slot-array payload bytes, excluding managed object headers.</summary>
    public ulong CpuBytes => checked((ulong)(m_read.Length + (m_rows.Length * (sizeof(int) + (2 * IntPtr.Size)))));

    /// <summary>Grows only a completed frame slot's counter/readback pair. The caller must finish and read that
    /// slot's previous submission first. Equal or smaller demand creates nothing; allocation failure keeps the old pair.</summary>
    /// <param name="slot">The waited frame slot.</param>
    /// <param name="rows">The required physical and detail rows, at least one.</param>
    public void EnsureRows(int slot, int rows) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
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
