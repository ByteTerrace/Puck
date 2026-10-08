using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Formats;

/// <summary>One strictly versioned format in <c>FormatVersions.json</c>.</summary>
/// <param name="Id">The format's name: its declaring type and member, such as <c>WorldFederationCodec.WireKey</c>.</param>
/// <param name="Source">The repository-relative path of the file that declares the token.</param>
/// <param name="Shape">A digest of the format's canonical source (<see cref="FormatShapeClosure"/>).</param>
/// <param name="Token">The format's current token.</param>
/// <param name="Open">The repository members the format's closure calls that are neither covered by the shape nor marked
/// (<see cref="FormatShapeClosure"/>), by documentation-comment id in ordinal order. A recorded call can leave and never
/// join.</param>
public sealed record FormatEntry(string Id, string Source, string Shape, string Token, IReadOnlyList<string> Open);
/// <summary>
/// <c>FormatVersions.json</c>: every strictly versioned wire, persisted, or cache format the source declares, its current
/// token, and the file that declares it. The ledger is generated from the source: <see cref="Discover"/> reads every
/// declaration of a recognized shape and <c>puck formats</c> writes the result, so the constants stay the one source
/// of truth and the ledger is their checked-in mirror.
/// <para>
/// The ledger records each format's shape: a digest of its canonical source on a line of its own. The shape, not the
/// token, is what tells two layouts apart, so each codec writes it (through the generated <c>FormatShapes.g.cs</c>,
/// <see cref="FormatShapesFiles"/>) and refuses any other, and a token is never demanded to move. A branch that edits a
/// codec moves its digest and fails <c>puck formats --check</c> until the author re-records it. Two branches that edit one
/// codec differently write different digest lines, which conflict, even when their token lines would merge cleanly.
/// </para>
/// </summary>
public static partial class FormatVersionsLedger {
    /// <summary>The ledger's file name at the repository root.</summary>
    public const string FileName = "FormatVersions.json";
    /// <summary>The ledger's own shape version.</summary>
    public const int Format = 3;

    // Members that carry a binary format's token when their initializer holds exactly one literal. A document
    // schema is recognised by its value instead (SchemaToken), whatever the member is called.
    private static readonly HashSet<string> TokenMembers = new(comparer: StringComparer.Ordinal) {
        "AbiVersion",
        "CompilerVersion",
        "CurrentVersion",
        "Format",
        "FormatVersion",
        "JournalMagic",
        "JournalVersion",
        "Magic",
        "PackMagic",
        "PackVersion",
        "ProtocolKey",
        "Revision",
        "ShapeToken",
        "SupportedFormat",
        "SupportedVersion",
        "TokenAlgorithm",
        "Version",
        "WireKey",
        "WireProtocolKey",
    };

    [GeneratedRegex(pattern: @"^puck\.[a-z0-9]+([.-][a-z0-9]+)*\.v[0-9]+\z", options: RegexOptions.CultureInvariant)]
    private static partial Regex SchemaToken();
    private static bool IsTokenField(MemberDeclarationSyntax member) {
        var modifiers = member.Modifiers.Select(selector: static modifier => modifier.Text).ToHashSet(comparer: StringComparer.Ordinal);

        return member switch {
            FieldDeclarationSyntax => (modifiers.Contains(item: "const") || (modifiers.Contains(item: "static") && modifiers.Contains(item: "readonly"))),
            PropertyDeclarationSyntax property => (modifiers.Contains(item: "static") || (property.ExpressionBody is not null)),
            _ => false,
        };
    }
    private static IEnumerable<(string Name, ExpressionSyntax Value)> Initializers(MemberDeclarationSyntax member) {
        switch (member) {
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables) {
                    if (variable.Initializer is { } initializer) {
                        yield return (variable.Identifier.Text, initializer.Value);
                    }
                }

                break;
            case PropertyDeclarationSyntax property:
                if (property.ExpressionBody is { } body) {
                    yield return (property.Identifier.Text, body.Expression);
                } else if (property.Initializer is { } propertyInitializer) {
                    yield return (property.Identifier.Text, propertyInitializer.Value);
                }

                break;
            default:
                break;
        }
    }
    // A numeric token whose bytes, least significant first, are printable ASCII and long enough to be a name rather
    // than a count is a four-character code or key, and is spelled as that text: 0x354445464B435550 is "PUCKFED5".
    private static string SpellNumber(object? value) {
        var number = Convert.ToUInt64(
            provider: CultureInfo.InvariantCulture,
            value: value
        );
        var bytes = new List<byte>();

        for (var rest = number; (rest != 0); rest >>= 8) {
            bytes.Add(item: ((byte)(rest & 0xFF)));
        }

        return (((bytes.Count >= 4) && bytes.All(predicate: static item => ((item >= 0x20) && (item <= 0x7E))))
            ? Encoding.ASCII.GetString(bytes: [.. bytes])
            : number.ToString(provider: CultureInfo.InvariantCulture));
    }
    private static bool TryTokenOf(string memberName, ExpressionSyntax value, out string token) {
        while (value is ParenthesizedExpressionSyntax parentheses) { value = parentheses.Expression; }
        var tokens = value.DescendantTokens().ToArray();
        var strings = tokens.Where(predicate: static item => item.IsKind(kind: SyntaxKind.StringLiteralToken)).ToArray();

        token = string.Empty;

        if (
            (value is LiteralExpressionSyntax) &&
            (strings.Length == 1) &&
            SchemaToken().IsMatch(input: strings[0].ValueText)
        ) {
            token = strings[0].ValueText;

            return true;
        }
        if (!TokenMembers.Contains(item: memberName)) {
            return false;
        }

        var spelled = tokens.Where(predicate: static item => (item.IsKind(kind: SyntaxKind.StringLiteralToken) || item.IsKind(kind: SyntaxKind.Utf8StringLiteralToken) || item.IsKind(kind: SyntaxKind.NumericLiteralToken))).ToArray();

        if (spelled.Length != 1) {
            return false;
        }

        // A string that names a version spells a number; an export name or a message that happens to share a member
        // name does not.
        if (
            !spelled[0].IsKind(kind: SyntaxKind.NumericLiteralToken) &&
            !spelled[0].ValueText.Any(predicate: char.IsAsciiDigit) &&
            !spelled[0].IsKind(kind: SyntaxKind.Utf8StringLiteralToken)
        ) {
            return false;
        }

        token = (spelled[0].IsKind(kind: SyntaxKind.NumericLiteralToken)
            ? SpellNumber(value: spelled[0].Value)
            : spelled[0].ValueText);

        return true;
    }

    /// <summary>Finds every strict format the source declares.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    /// <returns>The formats in ordinal id order. A declaring type and member that two files share are told apart by
    /// appending <c>@</c> and the path to both ids.</returns>
    public static IReadOnlyList<FormatEntry> Discover(IReadOnlyDictionary<string, string> files) => Close(files: files, only: null).Select(selector: static pair => pair.Entry).ToArray();

    /// <summary>Closes one format over its boundary, to show what its shape covers and what it leaves open.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    /// <param name="id">The format's ledger id.</param>
    /// <returns>The entry and its closure, or <see langword="null"/> when no format has that id.</returns>
    /// <exception cref="FormatBoundaryException">A seam gives no reason.</exception>
    public static (FormatEntry Entry, FormatClosure Closure)? Explain(IReadOnlyDictionary<string, string> files, string id) => Close(files: files, only: id).Select(selector: static pair => (((FormatEntry, FormatClosure)?)pair)).FirstOrDefault();

    private static IReadOnlyList<(FormatEntry Entry, FormatClosure Closure)> Close(IReadOnlyDictionary<string, string> files, string? only) {
        var found = new List<(string Id, string Source, string Token, string Owner, string Member)>();

        foreach (var (path, text) in files.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            // A Post stage's magic numbers frame its test ROMs and probes, not a format the engine reads back.
            if (path.Contains(
                comparisonType: StringComparison.Ordinal,
                value: ".Post/"
            )) {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(
                options: CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview),
                text: text
            ).GetRoot();

            foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(predicate: static member => IsTokenField(member: member))) {
                if (member.Parent is not BaseTypeDeclarationSyntax owner) {
                    continue;
                }

                foreach (var (name, value) in Initializers(member: member)) {
                    if (TryTokenOf(
                        memberName: name,
                        token: out var token,
                        value: value
                    )) {
                        found.Add(item: ($"{owner.Identifier.Text}.{name}", path, token, owner.Identifier.Text, name));
                    }
                }
            }
        }

        var shared = found.GroupBy(
            keySelector: static item => item.Id,
            comparer: StringComparer.Ordinal
        ).Where(predicate: static group => (group.Count() > 1)).Select(selector: static group => group.Key).ToHashSet(comparer: StringComparer.Ordinal);

        var named = found.Select(selector: item => (Item: item, Id: (shared.Contains(item: item.Id)
            ? $"{item.Id}@{item.Source}"
            : item.Id))).Where(predicate: pair => ((only is null) || (pair.Id == only))).OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static pair => pair.Id
        ).ToArray();
        var closures = new FormatShapeClosure(files: files).Of(formats: [.. named.Select(selector: static pair => new FormatRef(Id: pair.Id, Member: pair.Item.Member, Owner: pair.Item.Owner, Source: pair.Item.Source))], allIds: [.. found.Select(selector: item => (shared.Contains(item: item.Id) ? $"{item.Id}@{item.Source}" : item.Id))]);

        return [.. named.Select(selector: (pair, index) => (new FormatEntry(
            Id: pair.Id,
            Open: closures[index].Open,
            Shape: closures[index].Shape,
            Source: pair.Item.Source,
            Token: pair.Item.Token
        ), closures[index]))];
    }

    /// <summary>Renders the ledger in its one spelling: entries in ordinal id order, members in ordinal order, four-space
    /// indentation, one value per line, one final line feed.</summary>
    /// <param name="entries">The formats.</param>
    /// <returns>The ledger text.</returns>
    public static string Render(IReadOnlyList<FormatEntry> entries) {
        var builder = new StringBuilder();

        builder.Append(value: $"{{\n    \"format\": {Format.ToString(provider: CultureInfo.InvariantCulture)},\n    \"formats\": {{\n");

        for (var index = 0; (index < entries.Count); index++) {
            var entry = entries[index];

            builder.Append(value: $"        {Quote(text: entry.Id)}: {{\n");

            if (entry.Open.Count != 0) {
                builder.Append(value: "            \"open\": [\n");

                for (var call = 0; (call < entry.Open.Count); call++) {
                    builder.Append(value: $"                {Quote(text: entry.Open[call])}{((call == (entry.Open.Count - 1)) ? string.Empty : ",")}\n");
                }

                builder.Append(value: "            ],\n");
            }

            builder.Append(value: $"            \"shape\": {Quote(text: entry.Shape)},\n");

            builder.Append(value: $"            \"source\": {Quote(text: entry.Source)},\n");
            builder.Append(value: $"            \"token\": {Quote(text: entry.Token)}\n");
            builder.Append(value: ((index == (entries.Count - 1))
                ? "        }\n"
                : "        },\n"));
        }

        builder.Append(value: "    }\n}\n");

        return builder.ToString();
    }

    private static string Quote(string text) => JsonSerializer.Serialize(
        options: new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
        value: text
    );

    /// <summary>Parses a recorded ledger strictly.</summary>
    /// <param name="json">The ledger text.</param>
    /// <param name="entries">The recorded formats, or empty.</param>
    /// <param name="error">Why the text is unusable, or empty.</param>
    /// <returns><see langword="true"/> when the text is a ledger.</returns>
    public static bool TryParse(string json, out IReadOnlyList<FormatEntry> entries, out string error) {
        var parsed = new List<FormatEntry>();

        entries = parsed;

        try {
            using var document = JsonDocument.Parse(json: json, options: new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;

            if (
                (root.ValueKind != JsonValueKind.Object) ||
                !root.EnumerateObject().Select(selector: static member => member.Name).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["format", "formats"]) ||
                !root.GetProperty(propertyName: "format").TryGetInt32(value: out var format) ||
                (format != Format) ||
                (root.GetProperty(propertyName: "formats").ValueKind != JsonValueKind.Object)
            ) {
                error = $"the ledger must be an object of exactly format ({Format}) and formats";

                return false;
            }

            foreach (var member in root.GetProperty(propertyName: "formats").EnumerateObject()) {
                if (
                    (member.Value.ValueKind != JsonValueKind.Object) ||
                    !member.Value.EnumerateObject().Select(selector: item => item.Name).Where(predicate: static name => (name != "open")).Order(comparer: StringComparer.Ordinal).SequenceEqual(second: ["shape", "source", "token"]) ||
                    member.Value.EnumerateObject().Any(predicate: static item => ((item.Name != "open") && (item.Value.ValueKind != JsonValueKind.String))) ||
                    (member.Value.TryGetProperty(propertyName: "open", value: out var open) && ((open.ValueKind != JsonValueKind.Array) || open.EnumerateArray().Any(predicate: static call => (call.ValueKind != JsonValueKind.String))))
                ) {
                    error = $"'{member.Name}' must be an object of exactly the string members shape, source and token, and optionally open, an array of strings";

                    return false;
                }

                parsed.Add(item: new FormatEntry(
                    Id: member.Name,
                    Open: (member.Value.TryGetProperty(propertyName: "open", value: out var calls)
                        ? [.. calls.EnumerateArray().Select(selector: static call => call.GetString()!)]
                        : []),
                    Shape: member.Value.GetProperty(propertyName: "shape").GetString()!,
                    Source: member.Value.GetProperty(propertyName: "source").GetString()!,
                    Token: member.Value.GetProperty(propertyName: "token").GetString()!
                ));
            }

            error = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException)) {
            error = exception.Message;

            return false;
        }
    }

    private static string Named(IReadOnlyList<string> calls) => ((calls.Count == 0)
        ? string.Empty
        : $" ({string.Join(separator: ", ", values: calls.Take(count: 3))}{((calls.Count > 3) ? ", …" : string.Empty)})");

    /// <summary>The <c>--check</c> verdict on a recorded ledger against what the source declares now.</summary>
    /// <param name="recorded">The recorded formats.</param>
    /// <param name="recordedText">The ledger file's text.</param>
    /// <param name="current">The formats <see cref="Discover"/> finds now.</param>
    /// <returns>Every problem, each naming its fix; empty when the ledger holds.</returns>
    public static IReadOnlyList<string> Check(IReadOnlyList<FormatEntry> recorded, string recordedText, IReadOnlyList<FormatEntry> current) {
        var problems = new List<string>();
        var recordedById = recorded.ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        );
        var currentById = current.ToDictionary(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        );

        foreach (var entry in current) {
            if (!recordedById.TryGetValue(
                key: entry.Id,
                value: out var was
            )) {
                problems.Add(item: $"unrecorded: '{entry.Id}' ({entry.Source}) declares {entry.Token} and the ledger does not list it");
            } else if (!string.Equals(
                a: was.Token,
                b: entry.Token,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"retokened: '{entry.Id}' ({entry.Source}) declares {entry.Token} but the ledger records {was.Token}; run 'puck formats' to record it");
            } else if (!string.Equals(
                a: was.Shape,
                b: entry.Shape,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"reshaped: the shape of '{entry.Id}' ({entry.Source}) is now {entry.Shape} and the ledger records {was.Shape}; run 'puck formats' to record it (its codec refuses any other shape by this fingerprint, so no token bump is owed)");
            } else if (!string.Equals(
                a: was.Source,
                b: entry.Source,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"moved: '{entry.Id}' is declared in {entry.Source} but the ledger records {was.Source}");
            }
        }
        foreach (var entry in recorded.Where(predicate: entry => !currentById.ContainsKey(key: entry.Id))) {
            problems.Add(item: $"stale: '{entry.Id}' ({entry.Source}) is recorded but no longer declared");
        }

        foreach (var entry in current) {
            if (recordedById.TryGetValue(key: entry.Id, value: out var was)) {
                var added = entry.Open.Except(second: was.Open, comparer: StringComparer.Ordinal).ToArray();
                var removed = was.Open.Except(second: entry.Open, comparer: StringComparer.Ordinal).ToArray();

                if ((added.Length != 0) || (removed.Length != 0)) {
                    problems.Add(item: $"open: '{entry.Id}' ({entry.Source}) now reaches {added.Length} repository call(s) its shape does not cover that the ledger does not record{Named(calls: added)} and no longer reaches {removed.Length} it records{Named(calls: removed)}; run 'puck formats' to record the new boundary, or mark a call [FormatLeaf] if its behaviour decides a byte of the format, or [FormatSeam(\"its behaviour sets no byte because …\")] if it does not");
                }
            }
        }

        if (
            (problems.Count == 0) &&
            !string.Equals(
                a: recordedText,
                b: Render(entries: current),
                comparisonType: StringComparison.Ordinal
            )
        ) {
            problems.Add(item: $"not canonical: '{FileName}' differs from the form the writer produces");
        }

        return problems;
    }
}
