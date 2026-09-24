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

    /// <summary>Appends the lifetime line: every kind <paramref name="source"/> declares, with its total so far.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="source">The counter source, usually a node's <see cref="GpuWorkLedger"/>.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendLifetime(StringBuilder builder, IWorkCounterSource source) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        _ = builder.Append(value: "work lifetime:");

        foreach (var kind in source.WorkKinds) {
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
    /// (<see cref="AppendCompleted"/>), then, when the node counts object lifetimes, its lifetime line.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendNode(StringBuilder builder, GpuWorkNode node, GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sample);

        _ = builder.Append(value: "node ").Append(value: node.Name).Append(value: ' ');
        _ = AppendCompleted(
            builder: builder,
            sample: sample,
            source: node.Work
        );

        return ((node.Lifetime is { } lifetime)
            ? AppendLifetime(
                builder: builder,
                source: lifetime
            )
            : builder
        );
    }
    /// <summary>Writes one node as the JSON object
    /// <c>{"name":…,"sample":{"submission":S,"revision":R,"passes":[{"label":…,"state":"executed|skipped|not-reached","counts":{…}}],"outside":{…}}|null,"lifetime":{…}|null}</c>.
    /// A pass that did not execute carries no <c>counts</c>; <c>sample</c> is <see langword="null"/> while no submission
    /// has completed.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="node">The node to write.</param>
    /// <param name="sample">The caller's sample, overwritten by the read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static void WriteNode(Utf8JsonWriter writer, GpuWorkNode node, GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sample);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: node.Name
        );
        writer.WritePropertyName(propertyName: "sample");

        if (node.Work.TryReadCompleted(sample: sample) && (sample.Submission != 0L)) {
            WriteSample(
                sample: sample,
                writer: writer
            );
        } else {
            writer.WriteNullValue();
        }

        writer.WritePropertyName(propertyName: "lifetime");

        if (node.Lifetime is { } lifetime) {
            WorkCounterReport.WriteCounts(
                source: lifetime,
                writer: writer
            );
        } else {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }
    /// <summary>Appends one sample's lines: the submission line, one line per pass in pass order, then the outside
    /// line; or <c>work unavailable</c> when <paramref name="sample"/> holds no submission.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="sample">The sample to write.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="sample"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendSample(StringBuilder builder, GpuWorkSample sample) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sample);

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
            var state = sample.GetPassState(pass: pass);

            _ = builder.Append(value: "work ").Append(value: labels[pass]).Append(value: ' ').Append(value: EnumWireName<GpuPassState>.Of(value: state));

            if (state == GpuPassState.Executed) {
                _ = builder.Append(value: ':');

                for (var column = 0; (column < kinds.Length); column++) {
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
        }

        _ = builder.Append(value: "work outside:");

        for (var column = 0; (column < kinds.Length); column++) {
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
    private static void WriteSample(Utf8JsonWriter writer, GpuWorkSample sample) {
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
            var state = sample.GetPassState(pass: pass);

            writer.WriteStartObject();
            writer.WriteString(
                propertyName: "label",
                value: labels[pass]
            );
            writer.WriteString(
                propertyName: "state",
                value: EnumWireName<GpuPassState>.Of(value: state)
            );

            if (state == GpuPassState.Executed) {
                writer.WriteStartObject(propertyName: "counts");

                for (var column = 0; (column < kinds.Length); column++) {
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
            writer.WriteNumber(
                propertyName: kinds[column].Name,
                value: sample.GetOutsidePassCount(column: column)
            );
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
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
