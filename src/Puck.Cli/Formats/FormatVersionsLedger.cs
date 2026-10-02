using System.Globalization;
using System.Security.Cryptography;
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
/// <param name="Shape">A digest of the format's source (<see cref="FormatVersionsLedger.ShapeOf"/>), or
/// <see langword="null"/> for a named document schema, whose declaring file changes with every field it gains.</param>
/// <param name="Token">The format's current token.</param>
internal sealed record FormatEntry(string Id, string Source, string? Shape, string Token);
/// <summary>
/// <c>FormatVersions.json</c>: every strictly versioned wire, persisted, or cache format the source declares, its current
/// token, and the file that declares it. The ledger is generated from the source: <see cref="Discover"/> reads every
/// declaration of a recognized shape and <c>puck formats</c> writes the result, so the constants stay the one source
/// of truth and the ledger is their checked-in mirror.
/// <para>
/// A format bumped on two branches collides in the ledger's lines, not in the code: each entry spells its token on
/// its own line, and a binary format carries a digest of its declaring type's source on a line of its own. Two
/// branches that bump to the same new token merge their token lines cleanly, but they changed the codec differently,
/// so their digest lines differ and conflict. A branch that changes the codec without a bump moves the digest and
/// fails <c>puck formats --check</c> until the author re-records it, which is the moment to ask whether the encoding
/// changed and the token should too.
/// </para>
/// </summary>
internal static partial class FormatVersionsLedger {
    /// <summary>The ledger's file name at the repository root.</summary>
    public const string FileName = "FormatVersions.json";
    /// <summary>The ledger's own shape version.</summary>
    public const int Format = 1;

    // Members that carry a binary format's token when their initializer holds exactly one literal. A document
    // schema is recognised by its value instead (SchemaToken), whatever the member is called.
    private static readonly HashSet<string> TokenMembers = new(comparer: StringComparer.Ordinal) {
        "AbiVersion",
        "CompilerVersion",
        "CurrentVersion",
        "FormatVersion",
        "JournalMagic",
        "JournalVersion",
        "Magic",
        "PackMagic",
        "PackVersion",
        "ProtocolKey",
        "ShapeToken",
        "SupportedFormat",
        "SupportedVersion",
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

        for (var rest = number; rest != 0; rest >>= 8) {
            bytes.Add(item: (byte)(rest & 0xFF));
        }

        return (((bytes.Count >= 4) && bytes.All(predicate: static item => ((item >= 0x20) && (item <= 0x7E))))
            ? Encoding.ASCII.GetString(bytes: [.. bytes])
            : number.ToString(provider: CultureInfo.InvariantCulture));
    }
    private static bool TryTokenOf(string memberName, ExpressionSyntax value, out string token, out bool schema) {
        var tokens = value.DescendantTokens().ToArray();
        var strings = tokens.Where(predicate: static item => item.IsKind(kind: SyntaxKind.StringLiteralToken)).ToArray();

        token = string.Empty;
        schema = false;

        if (
            (strings.Length == 1) &&
            SchemaToken().IsMatch(input: strings[0].ValueText)
        ) {
            token = strings[0].ValueText;
            schema = true;

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
    private static string StemOf(string path) {
        var name = path[(path.LastIndexOf(value: '/') + 1)..];
        var dot = name.IndexOf(value: '.');

        return name[..dot];
    }
    private static string DirectoryOf(string path) => path[..(path.LastIndexOf(value: '/') + 1)];

    /// <summary>Digests the source that defines a binary format: every non-trivia token of the declaring file and of
    /// its partial siblings, <c>Stem.cs</c> and <c>Stem.*.cs</c> in the same directory, so a comment, a blank line, or
    /// a rename of nothing never moves it.</summary>
    /// <param name="files">Every source file's text, by repository-relative path.</param>
    /// <param name="source">The declaring file.</param>
    /// <returns>Sixteen lowercase hexadecimal digits of a SHA-256.</returns>
    public static string ShapeOf(IReadOnlyDictionary<string, string> files, string source) {
        var directory = DirectoryOf(path: source);
        var stem = StemOf(path: source);
        var builder = new StringBuilder();

        foreach (var path in files.Keys.Where(predicate: path => (
            path.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: directory
            ) &&
            (path.IndexOf(
                startIndex: directory.Length,
                value: '/'
            ) < 0) &&
            (path[directory.Length..].Equals(
                comparisonType: StringComparison.Ordinal,
                value: $"{stem}.cs"
            ) || path[directory.Length..].StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: $"{stem}."
            ))
        )).Order(comparer: StringComparer.Ordinal)) {
            foreach (var token in CSharpSyntaxTree.ParseText(
                options: CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview),
                text: files[path]
            ).GetRoot().DescendantTokens()) {
                builder.Append(value: token.Text).Append(value: '\n');
            }

            builder.Append(value: '\u0001');
        }

        return Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: builder.ToString())))[..16].ToLowerInvariant();
    }
    /// <summary>Finds every strict format the source declares.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    /// <returns>The formats in ordinal id order. A declaring type and member that two files share are told apart by
    /// appending <c>@</c> and the path to both ids.</returns>
    public static IReadOnlyList<FormatEntry> Discover(IReadOnlyDictionary<string, string> files) {
        var found = new List<(string Id, string Source, string Token, bool Schema)>();

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
                        schema: out var schema,
                        token: out var token,
                        value: value
                    )) {
                        found.Add(item: ($"{owner.Identifier.Text}.{name}", path, token, schema));
                    }
                }
            }
        }

        var shared = found.GroupBy(
            keySelector: static item => item.Id,
            comparer: StringComparer.Ordinal
        ).Where(predicate: static group => (group.Count() > 1)).Select(selector: static group => group.Key).ToHashSet(comparer: StringComparer.Ordinal);

        return [.. found.Select(selector: item => new FormatEntry(
            Id: (shared.Contains(item: item.Id)
                ? $"{item.Id}@{item.Source}"
                : item.Id),
            Shape: (item.Schema
                ? null
                : ShapeOf(
                    files: files,
                    source: item.Source
                )),
            Source: item.Source,
            Token: item.Token
        )).OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Id
        )];
    }
    /// <summary>Renders the ledger in its one spelling: entries in ordinal id order, members in ordinal order, four-space
    /// indentation, one value per line, one final line feed.</summary>
    /// <param name="entries">The formats.</param>
    /// <returns>The ledger text.</returns>
    public static string Render(IReadOnlyList<FormatEntry> entries) {
        var builder = new StringBuilder();

        builder.Append(value: $"{{\n    \"format\": {Format.ToString(provider: CultureInfo.InvariantCulture)},\n    \"formats\": {{\n");

        for (var index = 0; index < entries.Count; index++) {
            var entry = entries[index];

            builder.Append(value: $"        {Quote(text: entry.Id)}: {{\n");

            if (entry.Shape is not null) {
                builder.Append(value: $"            \"shape\": {Quote(text: entry.Shape)},\n");
            }

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
            using var document = JsonDocument.Parse(json: json);
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
                var names = member.Value.EnumerateObject().Select(selector: static item => item.Name).ToHashSet(comparer: StringComparer.Ordinal);

                if (
                    (member.Value.ValueKind != JsonValueKind.Object) ||
                    !names.IsSupersetOf(other: ["source", "token"]) ||
                    !names.IsSubsetOf(other: ["shape", "source", "token"]) ||
                    member.Value.EnumerateObject().Any(predicate: static item => (item.Value.ValueKind != JsonValueKind.String))
                ) {
                    error = $"'{member.Name}' must be an object of string members source, token, and optionally shape";

                    return false;
                }

                parsed.Add(item: new FormatEntry(
                    Id: member.Name,
                    Shape: (member.Value.TryGetProperty(
                        propertyName: "shape",
                        value: out var shape)
                        ? shape.GetString()
                        : null),
                    Source: member.Value.GetProperty(propertyName: "source").GetString()!,
                    Token: member.Value.GetProperty(propertyName: "token").GetString()!
                ));
            }

            error = string.Empty;

            return true;
        } catch (Exception exception) when (exception is JsonException or InvalidOperationException) {
            error = exception.Message;

            return false;
        }
    }
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
                problems.Add(item: $"bumped: '{entry.Id}' ({entry.Source}) declares {entry.Token} but the ledger records {was.Token}");
            } else if (!string.Equals(
                a: was.Shape,
                b: entry.Shape,
                comparisonType: StringComparison.Ordinal
            )) {
                problems.Add(item: $"reshaped: the source of '{entry.Id}' ({entry.Source}) changed while its token stayed {entry.Token}; if the encoding changed, bump the token, then record the new digest");
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
