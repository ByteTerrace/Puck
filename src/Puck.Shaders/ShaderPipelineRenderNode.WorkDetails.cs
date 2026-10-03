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

        if (rows <= counters.RowsOf(slot: slot)) { return; }
        var replacementBytes = checked(((((ulong)rows) * ((ulong)GpuKernelCounters.RowBytes)) * 2UL));
        var peak = checked((OwnedBytes + replacementBytes));

        if (peak > BudgetBytes) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' kernel work details need {peak} peak bytes; budget is {BudgetBytes}.");
        }
        var before = counters.TotalBytes;

        counters.EnsureRows(rows: rows, slot: slot);
        m_allocationBytes = checked(((m_allocationBytes + counters.TotalBytes) - before));
    }
}
