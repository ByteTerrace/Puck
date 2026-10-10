using System.Globalization;
using System.Text;
using Puck.World.Server;

namespace Puck.Cli.Determinism;

/// <summary>One document-level hash of a scenario: the world's fingerprint, its compiled-world key, one asset's
/// canonical hash, or one creation's bake key.</summary>
/// <param name="Name">The hash's name, unique in its scenario: <c>fingerprint</c>, <c>definition</c>,
/// <c>catalog</c>, <c>&lt;family&gt;:&lt;row&gt;</c> or <c>bake:&lt;prototype&gt;</c>.</param>
/// <param name="Value">The hash's text.</param>
public sealed record DeterminismDocumentHash(string Name, string Value);
/// <summary>One recorded scenario: its document-level hashes, then one hash vector per simulation tick, in
/// <see cref="DeterminismStream.Components"/> order.</summary>
/// <param name="Name">The scenario's name.</param>
/// <param name="Documents">The document-level hashes, in recorded order.</param>
/// <param name="Ticks">The per-tick hash vectors, the first for tick 1.</param>
public sealed record DeterminismScenarioRecord(string Name, IReadOnlyList<DeterminismDocumentHash> Documents, IReadOnlyList<ulong[]> Ticks) {
    /// <summary>Returns how many ticks changed a component's hash from the tick before.</summary>
    /// <param name="component">The component's name, one of <see cref="DeterminismStream.Components"/>.</param>
    /// <returns>The number of changes, zero for a component the run never moved.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> names no component.</exception>
    public int Changes(string component) {
        var column = -1;

        for (var index = 0; (index < DeterminismStream.Components.Count); index++) {
            if (DeterminismStream.Components[index] == component) {
                column = index;
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value: column, paramName: nameof(component));

        var changes = 0;

        for (var tick = 1; (tick < Ticks.Count); tick++) {
            if (Ticks[tick][column] != Ticks[(tick - 1)][column]) {
                changes++;
            }
        }

        return changes;
    }
}
/// <summary>
/// A determinism stream (<c>puck.determinism.stream.v1</c>): what <c>puck determinism record</c> measured for every
/// scenario of one manifest, in the one text spelling this type renders and parses. A line is one fact. The header
/// names the version, the manifest's pin and the per-tick components, then each scenario lists its document hashes
/// and one line per tick of sixteen-digit hexadecimal hashes. Parsing is strict: anything the renderer would not
/// write is refused by line number.
/// </summary>
/// <param name="ManifestPin">The pin of the manifest the stream was recorded from.</param>
/// <param name="Scenarios">The recorded scenarios, in manifest order.</param>
public sealed record DeterminismStream(string ManifestPin, IReadOnlyList<DeterminismScenarioRecord> Scenarios) {
    /// <summary>The stream's version token, its first line.</summary>
    public const string Version = "puck.determinism.stream.v1";

    // The stream's first line: the version token, then the shape fingerprint puck formats records for the layout below.
    private const string FirstLine = ((Version + " ") + FormatShapes.DeterminismStreamVersion);

    /// <summary>Gets the authoritative components hashed one by one, in the authoritative order, without the seed
    /// and the tick, which carry no state of their own.</summary>
    public static IReadOnlyList<WorldStateHashComponent> TickComponents { get; } = [.. WorldStateHashComposition.Authoritative.Where(predicate: static component => (component is not (WorldStateHashComponent.Seed or WorldStateHashComponent.Tick)))];
    /// <summary>Gets the names of a tick vector's hashes, in order: each authoritative component on its own, then the
    /// population pose hash, then the authoritative state hash the replay tape verifies. The aggregates come last so
    /// the first differing hash of a divergent tick names the system that split rather than a sum over all of them.</summary>
    public static IReadOnlyList<string> Components { get; } = [
        .. TickComponents.Select(selector: static component => component.ToString()),
        "pose",
        "authoritative",
    ];
    /// <summary>Gets the document hashes every scenario attests first, in this order: the world's fingerprint, then the
    /// compiled world's definition hash and catalog fingerprint.</summary>
    public static IReadOnlyList<string> RequiredDocuments { get; } = ["fingerprint", "definition", "catalog"];
    /// <summary>Gets the families of the document hashes that may follow the required ones, each named
    /// <c>&lt;family&gt;:&lt;row&gt;</c>: an asset row's canonical hash, a creation's canonical hash, or its bake
    /// key.</summary>
    public static IReadOnlyList<string> DocumentFamilies { get; } = ["patch", "tune", "music", "table", "creation", "bake"];

    // Why a scenario's document hashes are not this version's attestation, or null: the required three first and in
    // order, then only the known families, each naming a row.
    private static string? DocumentRefusal(IReadOnlyList<DeterminismDocumentHash> documents) {
        for (var index = 0; (index < RequiredDocuments.Count); index++) {
            if ((index >= documents.Count) || (documents[index].Name != RequiredDocuments[index])) {
                return $"its document hashes must begin {string.Join(separator: ", ", values: RequiredDocuments)}, in that order; document {(index + 1)} is {((index < documents.Count) ? $"'{documents[index].Name}'" : "missing")}";
            }
        }

        for (var index = RequiredDocuments.Count; (index < documents.Count); index++) {
            var name = documents[index].Name;
            var colon = name.IndexOf(value: ':');

            if (
                (colon <= 0) ||
                (colon == (name.Length - 1)) ||
                !DocumentFamilies.Contains(value: name[..colon])
            ) {
                return $"document '{name}' is not {string.Join(separator: ", ", values: DocumentFamilies)} followed by ':' and a row";
            }
        }

        return null;
    }
    private static string Hex(ulong value) => value.ToString(
        format: "x16",
        provider: CultureInfo.InvariantCulture
    );
    private static bool IsToken(string text) => (
        (text.Length > 0) &&
        !text.Any(predicate: static character => (char.IsWhiteSpace(c: character) || char.IsControl(c: character)))
    );

    /// <summary>Renders the stream in its one spelling: LF line breaks and one final line feed.</summary>
    /// <returns>The stream's text.</returns>
    public string Render() {
        var builder = new StringBuilder();

        builder.Append(value: FirstLine).Append(value: '\n');
        builder.Append(value: "manifest ").Append(value: ManifestPin).Append(value: '\n');
        builder.Append(value: "components ").AppendJoin(separator: ' ', values: Components).Append(value: '\n');

        foreach (var scenario in Scenarios) {
            builder.Append(value: "scenario ").Append(value: scenario.Name).Append(value: ' ').Append(value: scenario.Ticks.Count.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '\n');

            foreach (var document in scenario.Documents) {
                builder.Append(value: "document ").Append(value: document.Name).Append(value: ' ').Append(value: document.Value).Append(value: '\n');
            }
            for (var tick = 0; (tick < scenario.Ticks.Count); tick++) {
                builder.Append(value: "tick ").Append(value: (tick + 1).ToString(provider: CultureInfo.InvariantCulture));

                foreach (var hash in scenario.Ticks[tick]) {
                    builder.Append(value: ' ').Append(value: Hex(value: hash));
                }

                builder.Append(value: '\n');
            }
        }

        builder.Append(value: "end\n");

        return builder.ToString();
    }
    /// <summary>Parses a stream strictly.</summary>
    /// <param name="text">The stream's text.</param>
    /// <param name="stream">The parsed stream, or <see langword="null"/>.</param>
    /// <param name="error">Why the text is not a stream of this version, naming the line, or empty.</param>
    /// <returns><see langword="true"/> when the text is a stream of this version.</returns>
    public static bool TryParse(string text, out DeterminismStream? stream, out string error) {
        stream = null;
        error = string.Empty;

        if (!text.EndsWith(value: '\n')) {
            error = "the stream does not end with a line feed";

            return false;
        }

        var lines = text[..^1].Split(separator: '\n');

        if (lines[0] != FirstLine) {
            error = (lines[0].StartsWith(comparisonType: StringComparison.Ordinal, value: $"{Version} ")
                ? $"line 1 names shape fingerprint '{lines[0][(Version.Length + 1)..]}', not {FormatShapes.DeterminismStreamVersion}; a stream of another shape is not compared"
                : $"line 1 is '{lines[0]}', not the version token {Version}; a stream of another version is not compared"
            );

            return false;
        }
        if (
            (lines.Length < 4) ||
            !lines[1].StartsWith(comparisonType: StringComparison.Ordinal, value: "manifest ") ||
            !IsToken(text: lines[1]["manifest ".Length..])
        ) {
            error = "line 2 must be 'manifest <pin>'";

            return false;
        }
        if (lines[2] != $"components {string.Join(separator: ' ', values: Components)}") {
            error = $"line 3 must name this version's components: components {string.Join(separator: ' ', values: Components)}";

            return false;
        }

        var scenarios = new List<DeterminismScenarioRecord>();
        var index = 3;

        while ((index < lines.Length) && (lines[index] != "end")) {
            var header = lines[index].Split(separator: ' ');

            if (
                (header.Length != 3) ||
                (header[0] != "scenario") ||
                !IsToken(text: header[1]) ||
                !int.TryParse(s: header[2], style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var tickCount) ||
                (tickCount < 1)
            ) {
                error = $"line {(index + 1)} must be 'scenario <name> <ticks>'";

                return false;
            }

            index++;

            var documents = new List<DeterminismDocumentHash>();

            while ((index < lines.Length) && lines[index].StartsWith(comparisonType: StringComparison.Ordinal, value: "document ")) {
                var parts = lines[index].Split(separator: ' ');

                if (
                    (parts.Length != 3) ||
                    !IsToken(text: parts[1]) ||
                    !IsToken(text: parts[2]) ||
                    documents.Any(predicate: document => (document.Name == parts[1]))
                ) {
                    error = $"line {(index + 1)} must be 'document <name> <hash>' with a name its scenario has not used";

                    return false;
                }

                documents.Add(item: new DeterminismDocumentHash(
                    Name: parts[1],
                    Value: parts[2]
                ));
                index++;
            }

            if (DocumentRefusal(documents: documents) is { } documentRefusal) {
                error = $"scenario '{header[1]}' before line {(index + 1)}: {documentRefusal}";

                return false;
            }

            var ticks = new List<ulong[]>(capacity: tickCount);

            for (var tick = 1; (tick <= tickCount); tick++, index++) {
                var parts = ((index < lines.Length) ? lines[index].Split(separator: ' ') : []);

                if (
                    (parts.Length != (Components.Count + 2)) ||
                    (parts[0] != "tick") ||
                    (parts[1] != tick.ToString(provider: CultureInfo.InvariantCulture))
                ) {
                    error = $"line {(index + 1)} must be 'tick {tick}' and {Components.Count} hashes";

                    return false;
                }

                var vector = new ulong[Components.Count];

                for (var component = 0; (component < vector.Length); component++) {
                    var hex = parts[(component + 2)];

                    if (
                        (hex.Length != 16) ||
                        hex.Any(predicate: static character => !(char.IsAsciiDigit(c: character) || ((character >= 'a') && (character <= 'f')))) ||
                        !ulong.TryParse(s: hex, style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture, result: out vector[component])
                    ) {
                        error = $"line {(index + 1)} hash {(component + 1)} must be sixteen lowercase hexadecimal digits";

                        return false;
                    }
                }

                ticks.Add(item: vector);
            }

            if (scenarios.Any(predicate: scenario => (scenario.Name == header[1]))) {
                error = $"scenario '{header[1]}' is recorded twice";

                return false;
            }

            scenarios.Add(item: new DeterminismScenarioRecord(
                Documents: documents,
                Name: header[1],
                Ticks: ticks
            ));
        }

        if (
            (index != (lines.Length - 1)) ||
            (scenarios.Count == 0)
        ) {
            error = ((scenarios.Count == 0)
                ? "the stream records no scenario"
                : $"line {(index + 1)} follows the last scenario but is not the final 'end'");

            return false;
        }

        stream = new DeterminismStream(
            ManifestPin: lines[1]["manifest ".Length..],
            Scenarios: scenarios
        );

        return true;
    }
}
