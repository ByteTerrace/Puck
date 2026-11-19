using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    private GpuWorkDetail[] m_workDetailRows = [];

    private void PrepareWorkDetails(in FrameContext context, GpuKernelCounters? counters, int slot) {
        var count = 0;

        foreach (var pass in m_passes) {
            var details = (pass.Package?.WorkDetails(context: in context) ?? []);

            if ((!pass.CountsKernelWork && (details.Count != 0)) || (details.Count < pass.WorkDetails.Count)) {
                throw new InvalidOperationException(message: $"Pass '{pass.Name}' has invalid kernel work details.");
            }
            for (var index = 0; (index < pass.WorkDetails.Count); index++) {
                if (pass.WorkDetails[index] != details[index]) {
                    throw new InvalidOperationException(message: $"Pass '{pass.Name}' changed a retained detail index.");
                }
            }
            for (var index = 0; (index < details.Count); index++) {
                if (details[index] == "plain") {
                    throw new InvalidOperationException(message: "The plain work detail is reserved for the ledger.");
                }
            }
            if (details.Count > pass.WorkDetails.Count) { pass.WorkDetails = details.ToArray(); }
            pass.WorkDetailRow = ((details.Count == 0) ? 0u : checked((uint)((m_passes.Length + count) + 1)));
            if (details.Count != 0) { count = checked(((count + details.Count) + 1)); }
        }
        if (m_workDetailRows.Length < count) { m_workDetailRows = new GpuWorkDetail[count]; }
        var next = 0;

        foreach (var pass in m_passes) {
            if (pass.WorkDetails.Count == 0) { continue; }
            m_workDetailRows[next++] = new GpuWorkDetail(Detail: "plain", Pass: pass.Index);
            for (var index = 0; (index < pass.WorkDetails.Count); index++) {
                m_workDetailRows[next++] = new GpuWorkDetail(Pass: pass.Index, Detail: pass.WorkDetails[index]);
            }
        }
        m_work.ConfigureDetails(details: m_workDetailRows.AsSpan(length: count, start: 0));
        if (counters is null) { return; }
        var rows = checked((m_passes.Length + count));

        if ((rows <= counters.RowsOf(slot: slot)) || TryGrowWorkDetails(counters: counters, rows: rows, slot: slot)) { return; }
        // The frame keeps the rows the slot has. A pass whose named rows lie past them reads detail row zero, which the
        // generated puckCountDetail treats as no details, so no kernel writes past the buffer; its named work goes
        // uncounted this frame, the ledger reads its rows as zero, and the next frame asks to grow again.
        var physical = ((uint)counters.RowsOf(slot: slot));

        foreach (var pass in m_passes) {
            if ((pass.WorkDetailRow != 0u) && ((pass.WorkDetailRow + ((uint)pass.WorkDetails.Count)) > physical)) { pass.WorkDetailRow = 0u; }
        }
        m_work.CountDetailsRefused();
    }
    // Grows a completed slot's counters to rows, reserving the replacement pair beside everything the instance owns
    // first. False, with the slot's buffers and the reservation unchanged, when the budget or the device pool has no room.
    private bool TryGrowWorkDetails(GpuKernelCounters counters, int rows, int slot) {
        var replacementBytes = checked((GpuKernelCounters.SlotBytesOf(rows: rows) * 2UL));

        if ((checked((OwnedBytes + replacementBytes)) > BudgetBytes) || !TryReserveMore(bytes: replacementBytes)) { return false; }
        var before = counters.TotalBytes;

        try {
            counters.EnsureRows(rows: rows, slot: slot);
            m_allocationBytes = checked(((m_allocationBytes + counters.TotalBytes) - before));
            return true;
        } catch (GpuMemoryRefusedException) {
            return false;
        } finally {
            SettleReservation();
        }
    }
}
