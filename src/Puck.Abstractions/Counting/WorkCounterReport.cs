using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Documents;

namespace Puck.Abstractions.Counting;

/// <summary>
/// Writes an <see cref="IWorkCounterSource"/> the way every counter readout prints it, so a console verb and a
/// collector read the same shape. The text form is the source's name on its own line, then one
/// <c>&lt;kind&gt; &lt;value&gt;</c> line per declared kind in the source's order, each line ending with a line feed.
/// The machine form is the JSON object <c>{"name":"&lt;name&gt;","counts":{"&lt;kind&gt;":&lt;value&gt;,…}}</c>, and
/// a kind's legend entry (<see cref="WriteKind"/>) carries its unit and <see cref="WorkClass"/>.
/// </summary>
public static class WorkCounterReport {
    /// <summary>The name of the section a readout prints its allocation reading under.</summary>
    public const string AllocationSection = "allocation";

    /// <summary>Determines whether a source or section name falls under a filter: the name is the filter, or begins
    /// with the filter followed by a dot. A filter therefore selects whole segments, never part of one.</summary>
    /// <param name="name">The source or section name.</param>
    /// <param name="filter">The filter a reader typed.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> falls under <paramref name="filter"/>.</returns>
    public static bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> filter) =>
        (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: filter
        ) && (
            (name.Length == filter.Length) ||
            (name[filter.Length] == '.')
        ));
    /// <summary>Appends a source's section: its name, then every kind it declares with the kind's total so far.</summary>
    /// <param name="builder">The text to append to.</param>
    /// <param name="source">The source to write.</param>
    /// <returns><paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public static StringBuilder AppendSection(StringBuilder builder, IWorkCounterSource source) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        _ = builder.Append(value: source.Name).Append(value: '\n');

        foreach (var kind in source.WorkKinds) {
            _ = source.TryRead(
                kind: kind,
                value: out var value
            );
            _ = builder.Append(value: kind.Name).Append(value: ' ').Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"{value}"
            ).Append(value: '\n');
        }

        return builder;
    }
    /// <summary>Writes every kind a source declares as the members of one JSON object, the kind's name mapped to its
    /// total so far.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="source">The source to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public static void WriteCounts(Utf8JsonWriter writer, IWorkCounterSource source) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);

        writer.WriteStartObject();

        foreach (var kind in source.WorkKinds) {
            _ = source.TryRead(
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
    /// <summary>Writes one kind's legend entry as the member <c>"&lt;name&gt;":{"unit":"&lt;unit&gt;","class":"&lt;class&gt;"}</c>
    /// of the object <paramref name="writer"/> has open, the class spelled by its wire name
    /// (<see cref="EnumWireName{TEnum}"/>).</summary>
    /// <param name="writer">The writer to write to, inside an open object.</param>
    /// <param name="kind">The kind to describe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="kind"/> is <see langword="null"/>.</exception>
    public static void WriteKind(Utf8JsonWriter writer, WorkKind kind) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(kind);

        writer.WriteStartObject(propertyName: kind.Name);
        writer.WriteString(
            propertyName: "unit",
            value: kind.Unit
        );
        writer.WriteString(
            propertyName: "class",
            value: EnumWireName<WorkClass>.Of(value: kind.Class)
        );
        writer.WriteEndObject();
    }
    /// <summary>Writes a source as the JSON object <c>{"name":…,"counts":{…}}</c>.</summary>
    /// <param name="writer">The writer to write to.</param>
    /// <param name="source">The source to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public static void WriteSource(Utf8JsonWriter writer, IWorkCounterSource source) {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "name",
            value: source.Name
        );
        writer.WritePropertyName(propertyName: "counts");
        WriteCounts(
            source: source,
            writer: writer
        );
        writer.WriteEndObject();
    }
}
