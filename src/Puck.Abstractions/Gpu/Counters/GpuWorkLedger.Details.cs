namespace Puck.Abstractions.Gpu;

public sealed partial class GpuWorkLedger {
    /// <summary>Captures named rows for the next submission before pass activity. Every detailed pass includes
    /// exactly one <c>plain</c> row for its work outside named details. Identities grow until passes are reconfigured;
    /// pending submissions retain their own immutable identities.</summary>
    /// <param name="details">Rows after the physical pass rows, in GPU row order; copied on change.</param>
    /// <exception cref="ArgumentException">A label is empty, duplicated, names an unknown pass, lacks a plain row,
    /// or removes an existing identity.</exception>
    /// <exception cref="InvalidOperationException">A pass has already started, skipped or stood.</exception>
    public void ConfigureDetails(ReadOnlySpan<GpuWorkDetail> details) {
        if (m_open is { HasPassActivity: true }) {
            throw new InvalidOperationException(message: "Details cannot change after pass activity.");
        }
        if (details.SequenceEqual(other: m_details)) { return; }
        for (var index = 0; (index < details.Length); index++) {
            var detail = details[index];

            if ((((uint)detail.Pass) >= ((uint)m_labels.Length)) || string.IsNullOrWhiteSpace(value: detail.Detail)) {
                throw new ArgumentException(message: "A detail needs a configured pass and a nonempty label.", paramName: nameof(details));
            }
            if (details[..index].Contains(value: detail) || !details.Contains(value: new GpuWorkDetail(Pass: detail.Pass, Detail: "plain"))) {
                throw new ArgumentException(message: "Detail labels must be unique within a pass and include plain.", paramName: nameof(details));
            }
        }
        foreach (var detail in m_details) {
            if (!details.Contains(value: detail)) {
                throw new ArgumentException(message: "Detail identities cannot shrink within a pass configuration.", paramName: nameof(details));
            }
        }
        m_details = details.ToArray();
        m_open?.BindDetails(details: m_details);
    }

    private static void ReconcileDetails(Record record) {
        var start = ((record.Labels.Length + 1) * Columns);

        for (var detail = 0; (detail < record.Details.Length); detail++) {
            var identity = record.Details[detail];

            if (identity.Detail != "plain") { continue; }
            var passRow = record.Counts.AsSpan(start: ((identity.Pass + 1) * Columns), length: Columns);
            var plainRow = record.Counts.AsSpan(length: Columns, start: (start + (detail * Columns)));

            for (var column = 0; (column < Columns); column++) { plainRow[column] += passRow[column]; }
            passRow.Clear();
        }
        for (var detail = 0; (detail < record.Details.Length); detail++) {
            var pass = record.Details[detail].Pass;

            for (var column = 0; (column < Columns); column++) {
                record.Counts[(((pass + 1) * Columns) + column)] += record.Counts[((start + (detail * Columns)) + column)];
            }
        }
    }
}
