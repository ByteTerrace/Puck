namespace Puck.Abstractions.Gpu;

public sealed partial class GpuWorkLedger {
    /// <summary>Gets retained count/state/detail array payload bytes plus current pass-label/class arrays, excluding borrowed strings and object headers.</summary>
    public ulong CpuArrayBytes {
        get {
            var bytes = checked((ulong)((m_labels.Length * IntPtr.Size) + (m_classes.Length * sizeof(byte))));

            bytes += DetailBytes(details: m_details);
            for (var index = 0; (index < m_records.Length); index++) {
                var record = m_records[index];

                bytes += checked((ulong)((record.Counts.Length * sizeof(long)) + (record.States.Length * sizeof(byte))));
                var seen = ReferenceEquals(objA: record.Details, objB: m_details);

                for (var previous = 0; ((previous < index) && !seen); previous++) {
                    seen = ReferenceEquals(objA: record.Details, objB: m_records[previous].Details);
                }
                if (!seen) { bytes += DetailBytes(details: record.Details); }
            }
            for (var index = 0; (index < m_snapshots.Length); index++) {
                var snapshot = m_snapshots[index];

                bytes += snapshot.CpuArrayBytes;
                var seen = ReferenceEquals(objA: snapshot.DetailRows, objB: m_details);

                foreach (var record in m_records) { seen |= ReferenceEquals(objA: snapshot.DetailRows, objB: record.Details); }
                for (var previous = 0; ((previous < index) && !seen); previous++) {
                    seen = ReferenceEquals(objA: snapshot.DetailRows, objB: m_snapshots[previous].DetailRows);
                }
                if (!seen) { bytes += DetailBytes(details: snapshot.DetailRows); }
            }
            return bytes;
        }
    }

    private static ulong DetailBytes(GpuWorkDetail[] details) => checked((((ulong)details.Length) *
        ((ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<GpuWorkDetail>())));

    /// <summary>Captures the next submission's detail identities before any pass starts. Equal identities reuse
    /// the immutable snapshot; changes never rename a record already in flight.</summary>
    /// <param name="details">Detail rows in GPU row order, after the physical pass rows; copied on change.</param>
    /// <exception cref="ArgumentException">A detail has no name, names an unknown pass, or duplicates its owner/name.</exception>
    /// <exception cref="InvalidOperationException">A pass has already been entered, skipped or retained.</exception>
    public void ConfigureDetails(ReadOnlySpan<GpuWorkDetail> details) {
        if (m_open is { HasPassActivity: true }) {
            throw new InvalidOperationException(message: "Details cannot change after a pass starts.");
        }
        if (details.SequenceEqual(other: m_details)) { return; }
        for (var index = 0; (index < details.Length); index++) {
            var detail = details[index];

            if ((((uint)detail.Pass) >= ((uint)m_labels.Length)) || string.IsNullOrEmpty(value: detail.Detail)) {
                throw new ArgumentException(message: "A kernel detail must name a configured pass and a nonempty detail.", paramName: nameof(details));
            }
            for (var previous = 0; (previous < index); previous++) {
                if (details[previous] == detail) {
                    throw new ArgumentException(message: $"Pass '{m_labels[detail.Pass]}' repeats detail '{detail.Detail}'.", paramName: nameof(details));
                }
            }
        }
        m_details = details.ToArray();
        m_open?.Rebind(classes: m_classes, details: m_details, labels: m_labels, revision: m_revision);
    }
}
