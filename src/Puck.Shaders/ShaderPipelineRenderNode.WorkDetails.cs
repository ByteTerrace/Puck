using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    private GpuWorkDetail[] m_workDetailRows = [];

    /// <summary>Gets retained counter readback, ledger and detail-metadata array payload bytes. Borrowed name strings
    /// and managed object headers are excluded; GPU counter/readback buffers are already in <see cref="OwnedBytes"/>.</summary>
    public ulong KernelCounterCpuBytes => checked(((m_work.CpuArrayBytes +
        (((ulong)m_workDetailRows.Length) * ((ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<GpuWorkDetail>()))) +
        ((m_passes.Length == 0) ? 0 : (m_passes[0].KernelCounters?.CpuBytes ?? 0))));

    // Called after this slot's fence has completed (and published its old readback), before clear and recording.
    private void PrepareWorkDetails(in FrameContext context, GpuKernelCounters? counters, int slot) {
        var count = 0;

        foreach (var pass in m_passes) {
            pass.WorkDetails = (pass.Package?.WorkDetails(context: in context) ?? []);
            if (!pass.CountsKernelWork && (pass.WorkDetails.Count != 0)) {
                throw new InvalidOperationException(message: $"Pass '{pass.Name}' declares work details without kernel counters.");
            }
            pass.WorkDetailRow = checked((uint)(m_passes.Length + count));
            count = checked((count + pass.WorkDetails.Count));
        }
        if (m_workDetailRows.Length < count) { m_workDetailRows = new GpuWorkDetail[count]; }
        var next = 0;

        foreach (var pass in m_passes) {
            for (var index = 0; (index < pass.WorkDetails.Count); index++) {
                m_workDetailRows[next++] = new GpuWorkDetail(Pass: pass.Index, Detail: pass.WorkDetails[index]);
            }
        }
        m_workDetailRows.AsSpan(start: count).Clear();
        m_work.ConfigureDetails(details: m_workDetailRows.AsSpan(length: count, start: 0));
        if (counters is null) { return; }
        var rows = checked((m_passes.Length + count));

        if (rows <= counters.RowsOf(slot: slot)) { return; }
        var replacementBytes = checked(((((ulong)rows) * ((ulong)GpuKernelCounters.RowBytes)) * 2UL));
        var peak = checked((OwnedBytes + replacementBytes));

        if (peak > BudgetBytes) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' kernel-counter details need {peak} peak bytes; budget is {BudgetBytes}.");
        }
        var before = counters.TotalBytes;

        counters.EnsureRows(rows: rows, slot: slot);
        m_allocationBytes = checked(((m_allocationBytes + counters.TotalBytes) - before));
    }
}
