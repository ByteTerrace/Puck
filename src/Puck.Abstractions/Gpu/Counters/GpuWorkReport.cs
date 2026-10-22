using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Writes a render node's GPU work as the text lines every console readout prints, so each verb that reports counted
/// work reads the same way. Each line starts with <c>work</c> and ends with a line feed:
/// <code>
/// work submission=S revision=R
/// work &lt;label&gt; executed: dispatches=N dispatches.indirect=N …
/// work &lt;label&gt; detail=&lt;detail&gt; executed: dispatches=N …
/// work &lt;label&gt; skipped
/// work &lt;label&gt; not-reached
/// work outside: dispatches=N …
/// work lifetime: created.pipelines=N …
/// </code>
/// or the single line <c>work unavailable</c> when no submission has completed since the node started, reset,
/// resized, or reconfigured. A count is written as its kind's name without the leading <c>gpu.</c> segment, and every
/// column of <see cref="GpuWork.SubmissionKinds"/> is written, zero or not, in column order. A skipped or not-reached
/// pass has no counts, which is different from counts of zero.
/// <para>
/// Writing allocates nothing once the builder has the capacity for the text.
/// </para>
/// <para>
/// The machine form of a node (<see cref="WriteNode"/>) carries the same facts as JSON, each count under its kind's
/// full name.
/// </para>
/// </summary>
public static class GpuWorkReport {
    /// <summary>The name of the section a readout prints its render nodes' work under.</summary>
    public const string Section = "gpu";

    private const string KindPrefix = "gpu.";

    // The columns of a summary line, in the order Total fills them.
    private static readonly string[] SummaryNames = ["draws", "vertices", "texels", "dispatches"];

    /// <summary>Appends the lifetime line: every kind <paramref name="source"/> declares, with its total so far.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="source">The counter source, usually a node's <see cref="GpuWorkLedger"/>.</param>
    /// <param name="selection">The counts to write, or <see langword="null"/> for every count; a line with no selected
    /// kind is omitted.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendLifetime(StringBuilder builder, IWorkCounterSource source, GpuWorkSelection? selection = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        selection ??= GpuWorkSelection.All;

        var any = false;

        foreach (var kind in source.WorkKinds) {
            any |= selection.IncludesLifetime(kind: kind.Name);
        }

        if (!any && (selection.Counter is not null)) {
            return builder;
        }

        _ = builder.Append(value: "work lifetime:");

        foreach (var kind in source.WorkKinds) {
            if (!selection.IncludesLifetime(kind: kind.Name)) { continue; }
            _ = source.TryRead(
                kind: kind,
                value: out var value
            );
            AppendCount(
                builder: builder,
                kind: kind,
                value: value
            );
        }

        return builder.Append(value: '\n');
    }
    /// <summary>Appends the newest completed submission's lines, or <c>work unavailable</c> when none has completed.
    /// Reading reuses <paramref name="sample"/>, so a caller that keeps one sample allocates nothing for the read.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="source">The node's work source.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static StringBuilder AppendCompleted(StringBuilder builder, IGpuWorkSource source, GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sample);

        return (source.TryReadCompleted(sample: sample)
            ? AppendSample(
                builder: builder,
                sample: sample
            )
            : builder.Append(value: "work unavailable\n")
        );
    }
    /// <summary>Appends one node's lines: <c>node &lt;name&gt; </c> followed by its newest completed submission's lines
    /// (<see cref="AppendCompleted"/>), then, when the node counts object lifetimes, its lifetime line, then, when it reports
    /// them, <c>owned-bytes=N</c> on a line of its own.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendNode(StringBuilder builder, GpuWorkNode node, GpuWorkSample sample) {
        _ = TryAppendNode(
            builder: builder,
            node: node,
            sample: sample,
            selection: GpuWorkSelection.All
        );

        return builder;
    }
    /// <summary>Appends one node's lines as <see cref="AppendNode"/> does, limited to <paramref name="selection"/>. A
    /// selection that names passes omits the node's lifetime and owned-bytes lines, and a count prefix omits the
    /// owned-bytes line and the lifetime kinds it does not select.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <param name="selection">The passes and counts to write.</param>
    /// <returns><see langword="false"/> and nothing appended when the selection names passes and the node's newest
    /// completed submission has none of them; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryAppendNode(StringBuilder builder, GpuWorkNode node, GpuWorkSample sample, GpuWorkSelection selection) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(selection);

        var completed = node.Work.TryReadCompleted(sample: sample);

        if (selection.NamesPasses && !(completed && SelectsAnyPass(
            sample: sample,
            selection: selection
        ))) {
            return false;
        }

        _ = builder.Append(value: "node ").Append(value: node.Name).Append(value: ' ');
        _ = (completed
            ? AppendSample(
                builder: builder,
                sample: sample,
                selection: selection
            )
            : builder.Append(value: "work unavailable\n")
        );

        if (selection.NamesPasses) { return true; }

        if (node.Lifetime is { } lifetime) {
            _ = AppendLifetime(
                builder: builder,
                selection: selection,
                source: lifetime
            );
        }

        if ((selection.Counter is null) && (node.OwnedBytes is { } owned)) {
            _ = builder.Append(value: "owned-bytes=").Append(value: owned).Append(value: '\n');
        }

        return true;
    }
    /// <summary>Appends one node's summary line, <c>node &lt;name&gt; draws=N vertices=N texels=N dispatches=N</c>, or
    /// <c>node &lt;name&gt; work unavailable</c> when no submission has completed. The counts sum the newest completed
    /// submission's selected passes and, when the selection names no pass, the work outside every pass; a pass's detail
    /// rows are part of its row and count once. <c>dispatches</c> is direct and indirect dispatches together, and
    /// <c>texels</c> is <see cref="GpuWork.TexelsWritten"/>.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="node">The node to summarize.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <param name="selection">The passes to sum.</param>
    /// <returns><see langword="false"/> and nothing appended when the selection names passes and the node has none of
    /// them; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryAppendSummary(StringBuilder builder, GpuWorkNode node, GpuWorkSample sample, GpuWorkSelection selection) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(selection);

        var completed = (node.Work.TryReadCompleted(sample: sample) && (sample.Submission != 0L));

        if (selection.NamesPasses && !(completed && SelectsAnyPass(
            sample: sample,
            selection: selection
        ))) {
            return false;
        }

        _ = builder.Append(value: "node ").Append(value: node.Name);

        if (!completed) {
            _ = builder.Append(value: " work unavailable\n");

            return true;
        }

        Span<long> totals = stackalloc long[SummaryNames.Length];

        Total(
            sample: sample,
            selection: selection,
            totals: totals
        );

        for (var index = 0; (index < SummaryNames.Length); index++) {
            _ = builder.Append(value: ' ').Append(value: SummaryNames[index]).Append(value: '=');
            AppendNumber(
                builder: builder,
                value: totals[index]
            );
        }

        _ = builder.Append(value: '\n');

        return true;
    }
    /// <summary>Writes one node's summary as the JSON object
    /// <c>{"name":…,"summary":{"draws":N,"vertices":N,"texels":N,"dispatches":N}|null}</c>, with
    /// the counts <see cref="TryAppendSummary"/> sums; <c>summary</c> is <see langword="null"/> while no submission has
    /// completed.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="node">The node to summarize.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <param name="selection">The passes to sum.</param>
    /// <returns><see langword="false"/> and nothing written when the selection names passes and the node has none of
    /// them; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryWriteSummary(Utf8JsonWriter writer, GpuWorkNode node, GpuWorkSample sample, GpuWorkSelection selection) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(selection);

        var completed = (node.Work.TryReadCompleted(sample: sample) && (sample.Submission != 0L));

        if (selection.NamesPasses && !(completed && SelectsAnyPass(
            sample: sample,
            selection: selection
        ))) {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: node.Name
        );
        writer.WritePropertyName(propertyName: "summary");

        if (completed) {
            Span<long> totals = stackalloc long[SummaryNames.Length];

            Total(
                sample: sample,
                selection: selection,
                totals: totals
            );
            writer.WriteStartObject();

            for (var index = 0; (index < SummaryNames.Length); index++) {
                writer.WriteNumber(
                    propertyName: SummaryNames[index],
                    value: totals[index]
                );
            }

            writer.WriteEndObject();
        } else {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();

        return true;
    }
    /// <summary>Writes one node as the JSON object
    /// <c>{"name":…,"sample":{"submission":S,"revision":R,"passes":[{"label":…,"class":"deterministic|per-backend-deterministic","state":"executed|skipped|standing|not-reached","details":[{"label":…,"counts":{…}}],"counts":{…}}],"outside":{…}}|null,"lifetime":{…}|null,"owned-bytes":N|null}</c>.
    /// A pass's <c>class</c> is <see cref="GpuWorkSample.GetPassClass"/>. A pass that did not execute carries no
    /// <c>counts</c> on either the pass or its details; <c>sample</c> is <see langword="null"/> while no submission has completed.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static void WriteNode(Utf8JsonWriter writer, GpuWorkNode node, GpuWorkSample sample) =>
        _ = TryWriteNode(
            node: node,
            sample: sample,
            selection: GpuWorkSelection.All,
            writer: writer
        );
    /// <summary>Writes one node as <see cref="WriteNode"/> does, limited to <paramref name="selection"/>: only the
    /// selected passes and counts. A selection that names passes writes <c>lifetime</c> and <c>owned-bytes</c> as
    /// <see langword="null"/> and no <c>outside</c> counts, and a count prefix writes <c>owned-bytes</c> as
    /// <see langword="null"/> and only the lifetime kinds it selects.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <param name="selection">The passes and counts to write.</param>
    /// <returns><see langword="false"/> and nothing written when the selection names passes and the node's newest
    /// completed submission has none of them; otherwise <see langword="true"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryWriteNode(Utf8JsonWriter writer, GpuWorkNode node, GpuWorkSample sample, GpuWorkSelection selection) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(selection);

        var completed = (node.Work.TryReadCompleted(sample: sample) && (sample.Submission != 0L));

        if (selection.NamesPasses && !(completed && SelectsAnyPass(
            sample: sample,
            selection: selection
        ))) {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: node.Name
        );
        writer.WritePropertyName(propertyName: "sample");

        if (completed) {
            WriteSample(
                sample: sample,
                selection: selection,
                writer: writer
            );
        } else {
            writer.WriteNullValue();
        }

        writer.WritePropertyName(propertyName: "lifetime");

        if (!selection.NamesPasses && (node.Lifetime is { } lifetime)) {
            if (selection.Counter is null) {
                WorkCounterReport.WriteCounts(
                    source: lifetime,
                    writer: writer
                );
            } else {
                writer.WriteStartObject();

                foreach (var kind in lifetime.WorkKinds) {
                    if (!selection.IncludesLifetime(kind: kind.Name)) { continue; }
                    _ = lifetime.TryRead(
                        kind: kind,
                        value: out var value
                    );
                    writer.WriteNumber(
                        propertyName: kind.Name,
                        value: value
                    );
                }

                writer.WriteEndObject();
            }
        } else {
            writer.WriteNullValue();
        }

        writer.WritePropertyName(propertyName: "owned-bytes");

        if (!selection.NamesPasses && (selection.Counter is null) && (node.OwnedBytes is { } owned)) {
            writer.WriteNumberValue(value: owned);
        } else {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();

        return true;
    }
    /// <summary>Appends one sample's lines: the submission line, one line per pass in pass order, then the outside
    /// line; or <c>work unavailable</c> when <paramref name="sample"/> holds no submission.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="sample">The sample to write.</param>
    /// <param name="selection">The passes and counts to write, or <see langword="null"/> for all; a selection that names
    /// passes omits the work outside passes.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendSample(StringBuilder builder, GpuWorkSample sample, GpuWorkSelection? selection = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sample);

        selection ??= GpuWorkSelection.All;

        if (sample.Submission == 0L) {
            return builder.Append(value: "work unavailable\n");
        }

        _ = builder.Append(value: "work submission=");
        AppendNumber(
            builder: builder,
            value: sample.Submission
        );
        _ = builder.Append(value: " revision=");
        AppendNumber(
            builder: builder,
            value: sample.Revision
        );
        _ = builder.Append(value: '\n');

        var labels = sample.PassLabels;
        var kinds = GpuWork.SubmissionKinds;

        for (var pass = 0; (pass < sample.PassCount); pass++) {
            if (!selection.IncludesPass(label: labels[pass])) { continue; }
            var state = sample.GetPassState(pass: pass);

            _ = builder.Append(value: "work ").Append(value: labels[pass]).Append(value: ' ').Append(value: EnumWireName<GpuPassState>.Of(value: state));

            if (state == GpuPassState.Executed) {
                _ = builder.Append(value: ':');

                for (var column = 0; (column < kinds.Length); column++) {
                    if (!selection.IncludesColumn(column: column)) { continue; }
                    _ = sample.TryGetPassCount(
                        column: column,
                        pass: pass,
                        value: out var value
                    );
                    AppendCount(
                        builder: builder,
                        kind: kinds[column],
                        value: value
                    );
                }
            }

            _ = builder.Append(value: '\n');
            for (var detail = 0; (detail < sample.Details.Length); detail++) {
                var identity = sample.Details[detail];

                if (identity.Pass != pass) { continue; }
                _ = builder.Append(value: "work ").Append(value: labels[pass]).Append(value: " detail=").Append(value: identity.Detail)
                    .Append(value: ' ').Append(value: EnumWireName<GpuPassState>.Of(value: state));
                if (state == GpuPassState.Executed) {
                    _ = builder.Append(value: ':');
                    for (var column = 0; (column < kinds.Length); column++) {
                        if (!selection.IncludesColumn(column: column)) { continue; }
                        _ = sample.TryGetDetailCount(column: column, detail: detail, value: out var value);
                        AppendCount(builder: builder, kind: kinds[column], value: value);
                    }
                }
                _ = builder.Append(value: '\n');
            }
        }

        if (selection.NamesPasses) { return builder; }
        _ = builder.Append(value: "work outside:");

        for (var column = 0; (column < kinds.Length); column++) {
            if (!selection.IncludesColumn(column: column)) { continue; }
            AppendCount(
                builder: builder,
                kind: kinds[column],
                value: sample.GetOutsidePassCount(column: column)
            );
        }

        return builder.Append(value: '\n');
    }

    // Writes one completed sample's object: its identity, each pass by label and state with an executed pass's
    // counts, and the counts outside every pass.
    private static void WriteSample(Utf8JsonWriter writer, GpuWorkSample sample, GpuWorkSelection selection) {
        var kinds = GpuWork.SubmissionKinds;
        var labels = sample.PassLabels;

        writer.WriteStartObject();
        writer.WriteNumber(
            propertyName: "submission",
            value: sample.Submission
        );
        writer.WriteNumber(
            propertyName: "revision",
            value: sample.Revision
        );
        writer.WriteStartArray(propertyName: "passes");

        for (var pass = 0; (pass < sample.PassCount); pass++) {
            if (!selection.IncludesPass(label: labels[pass])) { continue; }
            var state = sample.GetPassState(pass: pass);

            writer.WriteStartObject();
            writer.WriteString(
                propertyName: "label",
                value: labels[pass]
            );
            writer.WriteString(
                propertyName: "class",
                value: EnumWireName<WorkClass>.Of(value: sample.GetPassClass(pass: pass))
            );
            writer.WriteString(
                propertyName: "state",
                value: EnumWireName<GpuPassState>.Of(value: state)
            );
            writer.WriteStartArray(propertyName: "details");
            for (var detail = 0; (detail < sample.Details.Length); detail++) {
                var identity = sample.Details[detail];

                if (identity.Pass != pass) { continue; }
                writer.WriteStartObject();
                writer.WriteString(propertyName: "label", value: identity.Detail);
                if (state == GpuPassState.Executed) {
                    writer.WriteStartObject(propertyName: "counts");
                    for (var column = 0; (column < kinds.Length); column++) {
                        if (!selection.IncludesColumn(column: column)) { continue; }
                        _ = sample.TryGetDetailCount(column: column, detail: detail, value: out var value);
                        writer.WriteNumber(propertyName: kinds[column].Name, value: value);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (state == GpuPassState.Executed) {
                writer.WriteStartObject(propertyName: "counts");

                for (var column = 0; (column < kinds.Length); column++) {
                    if (!selection.IncludesColumn(column: column)) { continue; }
                    _ = sample.TryGetPassCount(
                        column: column,
                        pass: pass,
                        value: out var value
                    );
                    writer.WriteNumber(
                        propertyName: kinds[column].Name,
                        value: value
                    );
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject(propertyName: "outside");

        for (var column = 0; (column < kinds.Length); column++) {
            if (selection.NamesPasses || !selection.IncludesColumn(column: column)) { continue; }
            writer.WriteNumber(
                propertyName: kinds[column].Name,
                value: sample.GetOutsidePassCount(column: column)
            );
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
    // Whether the sample has a pass the selection includes.
    private static bool SelectsAnyPass(GpuWorkSample sample, GpuWorkSelection selection) {
        var labels = sample.PassLabels;

        for (var pass = 0; (pass < labels.Length); pass++) {
            if (selection.IncludesPass(label: labels[pass])) {
                return true;
            }
        }

        return false;
    }
    // Sums the summary columns over the selected passes of an executed sample, and the work outside every pass when
    // the selection names no pass. A detail row is part of its pass's row, so only pass rows are read.
    private static void Total(GpuWorkSample sample, GpuWorkSelection selection, Span<long> totals) {
        totals.Clear();

        ReadOnlySpan<int> columns = [GpuWork.DrawsColumn, GpuWork.VerticesColumn, GpuWork.TexelsWrittenColumn, GpuWork.DispatchesColumn];
        var labels = sample.PassLabels;

        for (var pass = 0; (pass < labels.Length); pass++) {
            if (!selection.IncludesPass(label: labels[pass])) { continue; }

            for (var index = 0; (index < columns.Length); index++) {
                _ = sample.TryGetPassCount(
                    column: columns[index],
                    pass: pass,
                    value: out var value
                );
                totals[index] += value;
            }

            _ = sample.TryGetPassCount(
                column: GpuWork.IndirectDispatchesColumn,
                pass: pass,
                value: out var indirect
            );
            totals[3] += indirect;
        }

        if (selection.NamesPasses) { return; }

        for (var index = 0; (index < columns.Length); index++) {
            totals[index] += sample.GetOutsidePassCount(column: columns[index]);
        }

        totals[3] += sample.GetOutsidePassCount(column: GpuWork.IndirectDispatchesColumn);
    }
    // Appends " <name>=<value>", the name without a leading "gpu." segment, straight into the builder.
    private static void AppendCount(StringBuilder builder, WorkKind kind, long value) {
        var name = kind.Name.AsSpan();

        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: KindPrefix
        )) {
            name = name[KindPrefix.Length..];
        }

        _ = builder.Append(value: ' ').Append(value: name).Append(value: '=');
        AppendNumber(
            builder: builder,
            value: value
        );
    }
    // Formats through a stack buffer rather than an interpolation handler, whose generic formatting boxes the value
    // until the JIT optimizes the call site.
    private static void AppendNumber(StringBuilder builder, long value) {
        Span<char> digits = stackalloc char[20];

        _ = value.TryFormat(
            charsWritten: out var written,
            destination: digits,
            provider: CultureInfo.InvariantCulture
        );
        _ = builder.Append(value: digits[..written]);
    }
}
