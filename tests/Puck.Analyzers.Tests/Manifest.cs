using System.Globalization;
using System.Text;

namespace Puck.Analyzers.Tests;

/// <summary>One entry to serialize into a harness <c>VerifiedCode.json</c>.</summary>
internal sealed record ManifestEntry {
    /// <summary>The manifest key, matching a <c>[VerifiedCode(id)]</c> argument.</summary>
    public required string Id { get; init; }

    /// <summary>The assembly recorded as responsible for this entry, and the one whose sweep reports it unclaimed.</summary>
    public string Assembly { get; init; } = Harness.DefaultAssemblyName;
    /// <summary>The recorded documentation-comment id of the declaration this entry was written for.</summary>
    public string Symbol { get; init; } = "M:Subject.Assembly.Subject.Target";
    /// <summary>The recorded fingerprint algorithm.</summary>
    public string Algorithm { get; init; } = "csharp-tokens-v1";

    /// <summary>The recorded fingerprint.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The recorded basis set, compared against the attribute's <c>Basis</c> when it carries one.</summary>
    public IReadOnlyList<string> Basis { get; init; } = ["exact-by-construction"];
    /// <summary>The documentation-comment ids sealed alongside the branded declaration.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    /// <summary>The recorded law ids.</summary>
    public IReadOnlyList<string> Laws { get; init; } = [];

    /// <summary>Extra members written verbatim into the entry object, for schema cases the reader has no field for.</summary>
    public string? ExtraMembers { get; init; }
}
/// <summary>Builds <c>VerifiedCode.json</c> texts, including the malformed and off-schema shapes the analyzer is asked to survive.</summary>
internal static class Manifest {
    /// <summary>A well-formed manifest with no entries; every brand in the compilation is then unrecorded.</summary>
    public const string Empty = "{\r\n    \"format\": 1,\r\n    \"entries\": {}\r\n}\r\n";
    /// <summary>A manifest that is not JSON at all.</summary>
    public const string Malformed = "{";

    /// <summary>A well-formed manifest at the current schema version.</summary>
    public static string Of(params ManifestEntry[] entries) =>
        Of(format: "1", entries: entries);
    /// <summary>A manifest whose root <c>format</c> is written verbatim, so a case can record a version the reader has never seen.</summary>
    public static string Of(string format, params ManifestEntry[] entries) {
        var builder = new StringBuilder();

        builder.Append(value: "{\r\n    \"format\": ").Append(value: format).Append(value: ",\r\n    \"entries\": {\r\n");

        for (var index = 0; (index < entries.Length); index++) {
            builder.Append(value: Serialize(entry: entries[index]));

            if (index < (entries.Length - 1)) {
                builder.Append(value: ',');
            }

            builder.Append(value: "\r\n");
        }

        builder.Append(value: "    }\r\n}\r\n");

        return builder.ToString();
    }
    /// <summary>Serializes one entry object, keyed by its id, at the manifest's usual indentation.</summary>
    public static string Serialize(ManifestEntry entry) {
        var builder = new StringBuilder();

        builder.Append(value: "        \"").Append(value: Escape(text: entry.Id)).Append(value: "\": {\r\n");
        builder.Append(value: "            \"assembly\": \"").Append(value: Escape(text: entry.Assembly)).Append(value: "\",\r\n");
        builder.Append(value: "            \"symbol\": \"").Append(value: Escape(text: entry.Symbol)).Append(value: "\",\r\n");
        builder.Append(value: "            \"algorithm\": \"").Append(value: Escape(text: entry.Algorithm)).Append(value: "\",\r\n");
        builder.Append(value: "            \"sha256\": \"").Append(value: Escape(text: entry.Sha256)).Append(value: "\",\r\n");
        builder.Append(value: "            \"basis\": ").Append(value: Array(values: entry.Basis)).Append(value: ",\r\n");
        builder.Append(value: "            \"dependencies\": ").Append(value: Array(values: entry.Dependencies)).Append(value: ",\r\n");

        if (entry.ExtraMembers is not null) {
            builder.Append(value: entry.ExtraMembers).Append(value: ",\r\n");
        }

        builder.Append(value: "            \"laws\": ").Append(value: Array(values: entry.Laws)).Append(value: "\r\n");
        builder.Append(value: "        }");

        return builder.ToString();
    }
    /// <summary>Reads back the <c>sha256</c> recorded under <paramref name="id"/>, addressing the entry structurally rather than by first match.</summary>
    public static string? RecordedHash(string json, string id) {
        var entries = json.IndexOf(comparisonType: StringComparison.Ordinal, value: "\"entries\"");

        if (entries < 0) {
            return null;
        }

        var key = json.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: entries, value: (("\"" + id) + "\""));

        if (key < 0) {
            return null;
        }

        var marker = json.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: key, value: "\"sha256\"");

        if (marker < 0) {
            return null;
        }

        var open = json.IndexOf(value: '"', startIndex: json.IndexOf(startIndex: marker, value: ':'));
        var close = json.IndexOf(startIndex: (open + 1), value: '"');

        return json.Substring(length: ((close - open) - 1), startIndex: (open + 1));
    }

    private static string Array(IReadOnlyList<string> values) =>
        (("[ " + string.Join(separator: ", ", values: values.Select(selector: value => (("\"" + Escape(text: value)) + "\"")))) + " ]");
    private static string Escape(string text) {
        var builder = new StringBuilder(capacity: text.Length);

        foreach (var character in text) {
            switch (character) {
                case '"': builder.Append(value: "\\\""); break;
                case '\\': builder.Append(value: "\\\\"); break;
                case '\n': builder.Append(value: "\\n"); break;
                case '\r': builder.Append(value: "\\r"); break;
                case '\t': builder.Append(value: "\\t"); break;
                default:
                    if (character < ' ') {
                        builder.Append(value: "\\u").Append(value: ((int)character).ToString(format: "x4", provider: CultureInfo.InvariantCulture));
                    } else {
                        builder.Append(value: character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
