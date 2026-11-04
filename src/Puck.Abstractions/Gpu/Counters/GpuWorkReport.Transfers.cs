using System.Text;
using System.Text.Json;

namespace Puck.Abstractions.Gpu;

public static partial class GpuWorkReport {
    /// <summary>The observation scope of the native render registry's transfer readout. Source producers and
    /// traffic recorded outside those ledgers are not counted; this is not physical bus traffic.</summary>
    public const string TransferScope = "registered-render-device-ledgers";

    /// <summary>Writes cumulative transfer work from one ledger, or null when the source cannot provide it.
    /// Domain identity must be preserved when aggregating nodes: different names can alias the same ledger.</summary>
    /// <param name="writer">The destination.</param><param name="node">The selected node.</param>
    public static void WriteTransfers(Utf8JsonWriter writer, GpuWorkNode node) =>
        WriteTransfers(writer, node.Name, node.Work.TryReadTransfers(out var totals) ? totals : null);

    /// <summary>Writes an already captured per-domain observation under a node alias.</summary>
    /// <param name="writer">The destination.</param><param name="name">The node alias.</param>
    /// <param name="captured">The coherent captured counts, or null when unsupported.</param>
    public static void WriteTransfers(Utf8JsonWriter writer, string name, GpuTransferTotals? captured) {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WritePropertyName("transfers");
        if (captured is { } totals) {
            writer.WriteStartObject();
            writer.WriteNumber("domain", totals.Domain);
            writer.WriteNumber("submitted-through", totals.SubmittedThrough);
            writer.WriteNumber("completed-through", totals.CompletedThrough);
            Write("recorded", totals.Recorded);
            Write("submitted", totals.Submitted);
            Write("completed", totals.Completed);
            Write("discarded", totals.Discarded);
            writer.WriteEndObject();
        } else { writer.WriteNullValue(); }
        writer.WriteEndObject();

        void Write(string name, GpuTransferCounts counts) {
            writer.WriteStartObject(name);
            writer.WriteNumber("host-write-operations", counts.HostWriteOperations);
            writer.WriteNumber("host-write-bytes", counts.HostWriteBytes);
            writer.WriteNumber("copy-operations", counts.CopyOperations);
            writer.WriteNumber("buffer-copy-bytes", counts.BufferCopyBytes);
            writer.WriteEndObject();
        }
    }

    /// <summary>Appends cumulative transfer work, preserving ledger identity and submission watermarks.</summary>
    /// <param name="builder">The destination.</param><param name="node">The selected node.</param>
    /// <returns>The destination.</returns>
    public static StringBuilder AppendTransfers(StringBuilder builder, GpuWorkNode node) =>
        AppendTransfers(builder, node.Name, node.Work.TryReadTransfers(out var totals) ? totals : null);

    /// <summary>Appends an already captured per-domain observation under a node alias.</summary>
    /// <param name="builder">The destination.</param><param name="name">The node alias.</param>
    /// <param name="captured">The captured counts, or null when unsupported.</param><returns>The destination.</returns>
    public static StringBuilder AppendTransfers(StringBuilder builder, string name, GpuTransferTotals? captured) {
        builder.Append("node ").Append(name).Append(" transfers");
        if (captured is not { } totals) { return builder.Append(" unavailable\n"); }
        builder.Append(" domain=").Append(totals.Domain).Append(" submitted-through=").Append(totals.SubmittedThrough)
            .Append(" completed-through=").Append(totals.CompletedThrough).Append('\n');
        Write("recorded", totals.Recorded); Write("submitted", totals.Submitted);
        Write("completed", totals.Completed); Write("discarded", totals.Discarded);
        return builder;

        void Write(string name, GpuTransferCounts counts) => builder.Append(" transfers ").Append(name)
            .Append(" host-write-operations=").Append(counts.HostWriteOperations).Append(" host-write-bytes=").Append(counts.HostWriteBytes)
            .Append(" copy-operations=").Append(counts.CopyOperations).Append(" buffer-copy-bytes=").Append(counts.BufferCopyBytes).Append('\n');
    }

    /// <summary>Captures each distinct work source once for a readout. Aliases of one ledger domain receive its
    /// first coherent observation even if another queue completion happens while this command enumerates nodes.
    /// This allocates only on the reporting path; it retains no sources after the readout.</summary>
    /// <param name="nodes">The nodes selected for this readout.</param>
    /// <returns>Captured counts keyed by source reference; null entries remain explicitly unsupported.</returns>
    public static IReadOnlyDictionary<IGpuWorkSource, GpuTransferTotals?> ReadTransfers(IEnumerable<GpuWorkNode> nodes) {
        var sources = new Dictionary<IGpuWorkSource, GpuTransferTotals?>(ReferenceEqualityComparer.Instance);
        var domains = new Dictionary<long, GpuTransferTotals>();
        foreach (var node in nodes) {
            if (sources.ContainsKey(node.Work)) { continue; }
            GpuTransferTotals? captured = null;
            if (node.Work.TryReadTransfers(out var totals)) {
                if (domains.TryGetValue(totals.Domain, out var first)) { totals = first; }
                else { domains.Add(totals.Domain, totals); }
                captured = totals;
            }
            sources.Add(node.Work, captured);
        }
        return sources;
    }

}
