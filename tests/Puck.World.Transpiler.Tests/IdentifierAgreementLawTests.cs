using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.State;
using Puck.Testing;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Sql;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The document printers held to the one identifier rule, <see cref="IdentifierSpelling"/>, by
/// <see cref="SpellingLaws"/>: the formatter's property and cell-key names, and every name the decompiler prints —
/// a state row, a cell key, a rule group, a set, a pattern, a document member's key, and a row it projects into the
/// <c>sql</c> dialect. A printed name reads back as itself through the compiler, and a printer writes a name bare
/// exactly when the compiler reads that bare spelling as the name and the rule, less the position's reservations,
/// admits it. Each law carries its mutation proof. The state-language printers are held to the same laws in
/// <c>tests/Puck.State.Tests/IdentifierSpellingLawTests.cs</c>.</summary>
public sealed class IdentifierAgreementLawTests {
    private static readonly IReadOnlyList<string> FormatterCorpus = IdentifierCorpus.Names(count: 3000);
    // Each decompiler case compiles twice, so it sweeps a smaller share of the same seeded fuzz.
    private static readonly IReadOnlyList<string> DecompilerCorpus = IdentifierCorpus.Names(count: 400);

    private static bool IsCellName(string name) => CellName.TryParse(
        candidate: name,
        name: out _,
        reason: out _
    );
    // A row or cell key opening with the sigil is engine-minted, one carrying it further in or carrying the file joiner
    // anywhere is in a reserved form, and an author's is refused either way whichever way it is spelled.
    private static bool IsAuthoredCellName(string name) => (IsCellName(name: name) && !name.StartsWith(value: IdentifierSpelling.Sigil) && !GeneratedName.IsReserved(name: name));
    private static JsonObject? CompileQuietly(string source) {
        var compilation = WorldSources.Compile(source: source);

        return (compilation.Diagnostics.HasErrors
            ? null
            : compilation.Json
        );
    }
    private static JsonNode? At(JsonNode? node, params object[] path) {
        foreach (var step in path) {
            node = step switch {
                string member when (node is JsonObject @object) => @object[member],
                int index when ((node is JsonArray array) && (index < array.Count)) => array[index],
                _ => null,
            };
        }

        return node;
    }
    private static string? TextAt(JsonObject? document, params object[] path) => (((At(node: document, path: path) is JsonValue value) && value.TryGetValue<string>(value: out var text))
        ? text
        : null);
    private static bool HasLine(string printed, string pattern) => Regex.IsMatch(
        input: printed,
        options: RegexOptions.Multiline,
        pattern: pattern
    );
    // ---- The formatter.

    private static string Format(string source) => (PuckPrinter.Format(
        source: source,
        vocabulary: WorldDocumentVocabulary.Instance
    ).Value ?? string.Empty);
    private static DocumentNode? ParseQuietly(string source) {
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        return (parsed.Diagnostics.HasErrors
            ? null
            : parsed.Value
        );
    }
    private static string MemberSource(string spelling) => $"{WorldSources.Header}host {{\n    {spelling}: 1\n}}\n";
    private static string TopLevelSource(string spelling) => $"{WorldSources.Header}{spelling}: 1\n";
    private static string CellSource(string spelling) => $"{WorldSources.Header}state {{\n    world {{\n        table probe {{\n            {spelling} = 1\n        }}\n    }}\n}}\n";

    private static IReadOnlyList<SpellingPosition> FormatterPositions { get; } = [
        new SpellingPosition(
            Bare: static name => MemberSource(spelling: name),
            Carries: static name => (name.Length > 0),
            Label: "member property",
            Print: static name => Format(source: MemberSource(spelling: PuckStrings.Write(value: name))),
            PrintedBare: static (name, printed) => printed.Contains(comparisonType: StringComparison.Ordinal, value: $"\n  {name}: 1\n"),
            Quoted: static name => MemberSource(spelling: PuckStrings.Write(value: name)),
            Read: static text => ((ParseQuietly(source: text)?.Statements is [BlockNode { Identifier: "host", Statements: [PropertyNode property] }])
                ? property.Name
                : null
            ),
            Reserved: PuckPrinter.PropertyKeywords.Contains,
            Rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        new SpellingPosition(
            Bare: static name => TopLevelSource(spelling: name),
            Carries: static name => (name.Length > 0),
            Label: "top-level property",
            Print: static name => Format(source: TopLevelSource(spelling: PuckStrings.Write(value: name))),
            PrintedBare: static (name, printed) => printed.Contains(comparisonType: StringComparison.Ordinal, value: $"\n{name}: 1\n"),
            Quoted: static name => TopLevelSource(spelling: PuckStrings.Write(value: name)),
            Read: static text => ((ParseQuietly(source: text)?.Statements is [PropertyNode property])
                ? property.Name
                : null
            ),
            Reserved: static name => (PuckPrinter.PropertyKeywords.Contains(item: name) || PuckPrinter.DocumentHeaders.Contains(item: name)),
            Rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        new SpellingPosition(
            Bare: static name => CellSource(spelling: name),
            Carries: IsAuthoredCellName,
            Label: "table cell key",
            Print: static name => Format(source: CellSource(spelling: PuckStrings.Write(value: name))),
            PrintedBare: static (name, printed) => printed.Contains(comparisonType: StringComparison.Ordinal, value: $"\n      {name} = 1\n"),
            Quoted: static name => CellSource(spelling: PuckStrings.Write(value: name)),
            Read: static text => TextAt(document: CompileQuietly(source: text), "state", "world", 0, "cells", 0, "key"),
            Reserved: static _ => false,
            Rule: static name => IdentifierSpelling.IsName(text: name)
        ),
    ];

    // ---- The decompiler. Each case takes a small compiled document, renames one thing in it, decompiles it and
    // compiles the print back.

    private const string TableBody = "state {\n    world {\n        table probe {\n            a = 1\n        }\n    }\n}\n";
    private const string ExtensionBody = "extensions {\n    probeExt {\n        seat: 1\n    }\n}\n";
    private const string GroupBody = "state {\n    world {\n        slot flag = 0\n    }\n}\n\nstabilize settle maxPasses(4) {\n    rule \"collapse\" {\n        flag = 0\n    }\n}\n";
    private const string PatternBody = "pattern hand : Int {\n    value: \"face[$token]\"\n    symbols {\n        face = 1..2\n    }\n    match: face*\n}\n";
    private const string SetBody = (TableBody + "\nset lit: board(probe, 0..1)\n");
    private const string SqlBody = "state {\n    world {\n        slot flag = 0\n    }\n}\n";

    private static string Decompile(string body, Action<JsonObject> rename, bool sql = false) {
        var document = WorldSources.LowerClean(body: body);

        rename(obj: document);

        return WorldDecompiler.Decompile(
            root: document,
            sql: sql
        );
    }
    private static SpellingPosition Decompiled(string label, Func<string, bool> carries, string body, Action<JsonObject, string> rename, Func<string, string> bare, Func<string, string> barePattern, Func<JsonObject?, string?> read, Func<string, bool> rule, Func<string, bool>? reserved = null, bool sql = false) => new(
        Bare: bare,
        Carries: carries,
        Label: label,
        Print: name => Decompile(
            body: body,
            rename: document => rename(arg1: document, arg2: name),
            sql: sql
        ),
        PrintedBare: (name, printed) => HasLine(pattern: $"{barePattern(arg: Regex.Escape(str: name))}\r?$", printed: printed),
        Read: text => read(arg: CompileQuietly(source: text)),
        Reserved: (reserved ?? (static _ => false)),
        Rule: name => rule(arg: name)
    );

    private static IReadOnlyList<SpellingPosition> DecompilerPositions { get; } = [
        Decompiled(
            bare: static name => $"{WorldSources.Header}{TableBody.Replace(newValue: $"table {name}", oldValue: "table probe")}",
            barePattern: static name => $@"^\s*table {name} \{{",
            body: TableBody,
            carries: IsAuthoredCellName,
            label: "decompiled state row",
            read: static document => TextAt(document: document, "state", "world", 0, "name"),
            rename: static (document, name) => At(node: document, "state", "world", 0)!["name"] = name,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{TableBody.Replace(newValue: $"{name} = 1", oldValue: "a = 1")}",
            barePattern: static name => $@"^\s*{name} = 1",
            body: TableBody,
            carries: IsAuthoredCellName,
            label: "decompiled cell key",
            read: static document => TextAt(document: document, "state", "world", 0, "cells", 0, "key"),
            rename: static (document, name) => At(node: document, "state", "world", 0, "cells", 0)!["key"] = name,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{GroupBody.Replace(newValue: $"stabilize {name}", oldValue: "stabilize settle")}",
            barePattern: static name => $@"^\s*stabilize {name} .*",
            body: GroupBody,
            carries: IsAuthoredCellName,
            label: "decompiled rule group",
            read: static document => TextAt(document: document, "ruleGroups", 0, "name"),
            rename: static (document, name) => {
                At(node: document, "ruleGroups", 0)!["name"] = name;
                At(node: document, "ruleGroups", 0, "steps", 0)!["rule"] = GeneratedName.Append(name: name, part: "collapse");
                At(node: document, "rules", 0)!["name"] = GeneratedName.Append(name: name, part: "collapse");
            },
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{SetBody.Replace(newValue: $"set {name}", oldValue: "set lit")}",
            barePattern: static name => $@"^\s*set {name}: .*",
            body: SetBody,
            carries: IsAuthoredCellName,
            label: "decompiled set",
            read: static document => TextAt(document: document, "sets", 0, "name"),
            rename: static (document, name) => At(node: document, "sets", 0)!["name"] = name,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{PatternBody.Replace(newValue: $"pattern {name}", oldValue: "pattern hand")}",
            barePattern: static name => $@"^\s*pattern {name} : .*",
            body: PatternBody,
            carries: IsAuthoredCellName,
            label: "decompiled pattern",
            read: static document => TextAt(document: document, "patterns", 0, "name"),
            rename: static (document, name) => At(node: document, "patterns", 0)!["name"] = name,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{ExtensionBody.Replace(newValue: name, oldValue: "probeExt")}",
            barePattern: static name => $@"^\s*{name} \{{",
            body: ExtensionBody,
            carries: static name => (name.Length > 0),
            label: "decompiled member block",
            read: static document => ((At(node: document, "extensions") is JsonObject { Count: 1 } extensions)
                ? extensions.Single().Key
                : null
            ),
            rename: static (document, name) => {
                var extensions = ((JsonObject)document["extensions"]!);
                var member = extensions["probeExt"]!;

                _ = extensions.Remove(propertyName: "probeExt");
                extensions[name] = member;
            },
            reserved: PuckPrinter.PropertyKeywords.Contains,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}{ExtensionBody.Replace(newValue: name, oldValue: "seat")}",
            barePattern: static name => $@"^\s*{name}: 1",
            body: ExtensionBody,
            carries: static name => (name.Length > 0),
            label: "decompiled member property",
            read: static document => ((At(node: document, "extensions", "probeExt") is JsonObject { Count: 1 } member)
                ? member.Single().Key
                : null
            ),
            rename: static (document, name) => {
                var member = ((JsonObject)At(node: document, "extensions", "probeExt")!);
                var value = member["seat"]!;

                _ = member.Remove(propertyName: "seat");
                member[name] = value;
            },
            reserved: PuckPrinter.PropertyKeywords.Contains,
            rule: static name => IdentifierSpelling.IsName(text: name)
        ),
        Decompiled(
            bare: static name => $"{WorldSources.Header}sql {{\n    DECLARE {name} INT DEFAULT 0;\n}}\n",
            barePattern: static name => $@"^\s*DECLARE {name} .*",
            body: SqlBody,
            carries: IsAuthoredCellName,
            label: "decompiled sql row",
            read: static document => TextAt(document: document, "state", "world", 0, "name"),
            rename: static (document, name) => At(node: document, "state", "world", 0)!["name"] = name,
            reserved: static name => StateSqlLexer.IsReservedWord(word: name),
            rule: static name => IdentifierSpelling.IsIdentifier(text: name),
            sql: true
        ),
    ];

    private static void AssertNone(List<string> violations) => Assert.True(
        condition: (violations.Count == 0),
        userMessage: SpellingLaws.Describe(violations: violations)
    );
    private static SpellingPosition PositionOf(string label) => FormatterPositions.Concat(second: DecompilerPositions).Single(predicate: position => (position.Label == label));
    private static IReadOnlyList<string> CorpusOf(string label) => (FormatterPositions.Any(predicate: position => (position.Label == label))
        ? FormatterCorpus
        : DecompilerCorpus
    );

    public static TheoryData<string> PositionLabels() => [.. FormatterPositions.Concat(second: DecompilerPositions).Select(selector: static position => position.Label)];
    public static TheoryData<string> QuotedPositionLabels() => [.. FormatterPositions.Select(selector: static position => position.Label)];
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void APrintedNameCompilesBackAsItself(string label) => AssertNone(violations: SpellingLaws.RoundTripViolations(
        names: CorpusOf(label: label),
        position: PositionOf(label: label)
    ));
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void ANamePrintsBareExactlyWhenTheCompilerAndTheRuleTakeItBare(string label) => AssertNone(violations: SpellingLaws.AgreementViolations(
        names: CorpusOf(label: label),
        position: PositionOf(label: label)
    ));
    [MemberData(memberName: nameof(PositionLabels))]
    [Theory]
    public void TheRoundTripLawCatchesAPrinterCarryingTheUnicodeCopy(string label) => Assert.NotEmpty(collection: SpellingLaws.RoundTripViolations(
        names: CorpusOf(label: label),
        position: SpellingLaws.WithUnicodeCopy(position: PositionOf(label: label))
    ));
    [MemberData(memberName: nameof(QuotedPositionLabels))]
    [Theory]
    public void TheAgreementLawCatchesAPrinterThatQuotesTooMuch(string label) => Assert.NotEmpty(collection: SpellingLaws.AgreementViolations(
        names: CorpusOf(label: label),
        position: SpellingLaws.WithUnderscoresQuoted(position: PositionOf(label: label))
    ));
}
