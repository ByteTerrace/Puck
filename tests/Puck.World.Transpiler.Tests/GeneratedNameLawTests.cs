using System.Text.Json.Nodes;
using Puck.State;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The generated-name laws at the compiler, each with its mutation proof: every name the compiler mints —
/// a rule scope's members and groups, a test's worlds, verdict rows, witnesses, rules and recorded keys, a ground's
/// prototype, a door's return arch and a link's references — is in the reserved form
/// (<see cref="GeneratedName"/>); and every door an author declares a name through refuses both reserved characters,
/// bare or quoted, with PUCK113, so no author-written name is in either form. The joins themselves and the state catalog's rows are held to the
/// same laws in <c>tests/Puck.State.Tests/GeneratedNameLawTests.cs</c>.</summary>
public sealed class GeneratedNameLawTests {
    private const string Composition = """
        module plot(seed) {
            state {
                world {
                    slot hp = seed
                }
            }
        }
        module islandRoom() {
            prototypes [{ id: "arch", document {
                schema: "puck.creation.v1",
                palette [{ color: "#808080" }],
                shapes [{ id: 0, type: "Box", position [0, 0, 0], rotation [0, 0, 0, 1], scale [1, 2, 0.1], material: 0, blend: "Union", smooth: 0 }],
                behavior { faces [{ name: "portal", shapeId: 0 }] }
            } }]
            placements { rows [{
                id: "arch1", prototypeId: "arch", position [0, 0, 0], yawDegrees: 0, scale: 1,
                faceSources [{ face: "portal", source { "$type": "none" } }]
            }] }
            ground floor { size [12m, 8m] }
        }
        module parlorRoom() {
            spawn arrival { at [3m, 0m, 4m] yaw: 90deg }
            ground floor { center [12m, 0m, 0m] size [12m, 8m] }
        }
        world island = islandRoom()
        world parlor = parlorRoom()
        door island.arch1, parlor.arrival
        border island.floor.east, parlor.floor.west { height: 6m }
        """;
    private const string Rules = """
        state {
            world {
                slot flag = 0
                table board {
                    a = 1
                }
            }
        }

        rules dealing when flag == 0 {
            rule start {
                flag = 1
            }
        }

        stabilize settle maxPasses(4) {
            rule collapse {
                flag = 0
            }
        }

        workflow turn {
            step begin {
                flag = 1
            }
        }

        rules outer {
            stabilize inner maxPasses(2) {
                rule calm {
                    flag = 0
                }
            }
        }
        """;
    private const string Tests = """
        schema: "puck.world.definition.v1"
        documentId: "kinds"

        state {
            world {
                slot hp = 3
                slot speed = 1.5
                table board {
                    a = 1
                }
            }
        }

        test "kinds" {
            given {
                speed = 2.25
            }
            expect {
                speed > 2.0 and hp == 3
                board[a] == 1
            }
        }
        """;

    // The form a minted name must take: a generated document name, a generated file-backed name, or a reserved word
    // a row's own shape mints under the sigil.
    private enum Form {
        Document,
        File,
        Reserved,
    }
    // One minted name and where it was read from.
    private sealed record Minted(string Site, string Name, Form Form = Form.Document);

    private static WorldCompilation Green(string source, bool allowMultiple = false, string? sourcePath = null) {
        var compilation = WorldCompiler.Compile(
            allowMultiple: allowMultiple,
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: source,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation;
    }
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => ((node as JsonArray) ?? []).OfType<JsonObject>();
    private static string Text(JsonNode? node) => node!.GetValue<string>();
    private static IEnumerable<Minted> RuleScopeNames() {
        var document = Green(source: (WorldSources.Header + Rules)).RequireJson();

        foreach (var rule in Rows(node: document["rules"])) {
            yield return new Minted(Name: Text(node: rule["name"]), Site: "scope member rule");
        }
        foreach (var group in Rows(node: document["ruleGroups"])) {
            if ((Text(node: group["name"]) is var name) && (name != "settle") && (name != "turn")) {
                yield return new Minted(Name: name, Site: "nested group");
            }
            foreach (var step in Rows(node: group["steps"])) {
                yield return new Minted(Name: Text(node: step["rule"]), Site: "group step");
            }
        }
    }
    private static IEnumerable<Minted> TestNames() {
        var world = Assert.Single(collection: Green(source: Tests, sourcePath: "kinds.puck").TestWorlds);

        yield return new Minted(Form: Form.File, Name: world.Name, Site: "test world");
        foreach (var row in Rows(node: world.Json["state"]!["world"])) {
            if ((row["verdict"] is JsonObject verdict)) {
                yield return new Minted(Name: Text(node: row["name"]), Site: "verdict row");
                yield return new Minted(Form: Form.Reserved, Name: Text(node: verdict["status"]), Site: "status key");

                foreach (var cell in Rows(node: row["cells"])) {
                    if ((Text(node: cell["key"]) is var key) && key.StartsWith(value: "board")) {
                        yield return new Minted(Name: key, Site: "recorded keyed read");
                    }
                }
            }
            if (row["witness"] is not null) {
                yield return new Minted(Name: Text(node: row["name"]), Site: "witness row");
            }
        }
        foreach (var rule in Rows(node: world.Json["rules"])) {
            yield return new Minted(Name: Text(node: rule["name"]), Site: "verdict rule");
        }

        var composed = Green(allowMultiple: true, source: $"{TestWorldFixtures.InlineArmoury}\n\nuse armoury as left(plating: 2)\n\nentry world west = armoury(plating: 2)\n\ntest \"a composed claim\" {{\n    expect {{\n        west {{\n            armour == 2\n        }}\n    }}\n}}\n");

        foreach (var generated in composed.TestWorlds) {
            yield return new Minted(Form: Form.File, Name: generated.Name, Site: "composed or brought test world");

            foreach (var sibling in generated.Siblings) {
                yield return new Minted(Form: Form.File, Name: sibling.Name, Site: "sibling world");
            }
        }
    }
    private static IEnumerable<Minted> CompositionNames() {
        var compilation = Green(allowMultiple: true, source: Composition, sourcePath: "composition.puck");

        foreach (var output in compilation.Worlds) {
            foreach (var prototype in Rows(node: output.Json["prototypes"])) {
                if ((Text(node: prototype["id"]) is var id) && (id != "arch")) {
                    yield return new Minted(Name: id, Site: "ground prototype");
                }
            }
            foreach (var placement in Rows(node: output.Json["placements"]?["rows"])) {
                if ((Text(node: placement["id"]) is var id) && id.StartsWith(value: "return")) {
                    yield return new Minted(Name: id, Site: "door return arch");
                }
            }
            foreach (var reference in Rows(node: output.Json["references"]).Concat(second: Rows(node: output.Json["destinations"]))) {
                yield return new Minted(Name: Text(node: reference["name"]), Site: "link reference or destination");
            }
        }
    }
    private static List<string> MintingViolations(IEnumerable<Minted> minted) {
        var violations = new List<string>();
        var seen = 0;

        foreach (var (site, name, form) in minted) {
            seen++;

            if (!(form switch {
                Form.File => GeneratedName.IsGeneratedFile(name: name),
                Form.Reserved => name.StartsWith(value: IdentifierSpelling.Sigil),
                _ => GeneratedName.IsGenerated(name: name),
            })) {
                violations.Add(item: $"{site}: '{name}' is not in the {form} form reserved for it");
            }
        }
        if (seen == 0) {
            violations.Add(item: "no minted name was read, so the law saw nothing");
        }

        return violations;
    }
    private static void AssertNone(List<string> violations) => Assert.True(
        condition: (violations.Count == 0),
        userMessage: SpellingLaws.Describe(violations: violations)
    );

    [Fact]
    public void EveryNameTheCompilerMintsIsInTheReservedForm() => AssertNone(violations: MintingViolations(minted: [
        .. RuleScopeNames(),
        .. TestNames(),
        .. CompositionNames(),
    ]));
    // The mutation proof: the spellings a scope, a test and a door would mint by joining with an author character.
    [Fact]
    public void TheMintingLawCatchesASiteJoiningWithAnAuthorCharacter() {
        Assert.NotEmpty(collection: MintingViolations(minted: [new Minted(Name: "settle_collapse", Site: "scope member rule")]));
        Assert.NotEmpty(collection: MintingViolations(minted: [new Minted(Name: "kinds-1", Site: "verdict row")]));
        Assert.NotEmpty(collection: MintingViolations(minted: [new Minted(Form: Form.File, Name: "kinds--kinds", Site: "test world")]));
        Assert.NotEmpty(collection: MintingViolations(minted: [new Minted(Name: "return-island-arch1", Site: "door return arch")]));
        Assert.NotEmpty(collection: MintingViolations(minted: []));
    }

    // ---- Every author door: a position builds a source declaring one name there.

    private sealed record Door(string Label, Func<string, string> Source, bool File = false, bool Multiple = false, Func<string, string>? Path = null);

    private static IReadOnlyList<Door> Doors { get; } = [
        new(Label: "state row", Source: static name => $"{WorldSources.Header}state {{\n    world {{\n        row {{\n            cells [\n                {{\n                    key: \"a\"\n                    value: 1\n                }}\n            ]\n            kind: \"Int\"\n            name: {PuckStrings.Write(value: name)}\n        }}\n    }}\n}}\n"),
        new(Label: "table cell key", Source: static name => $"{WorldSources.Header}state {{\n    world {{\n        table probe {{\n            {PuckStrings.Write(value: name)} = 1\n        }}\n    }}\n}}\n"),
        new(Label: "rule", Source: static name => $"{WorldSources.Header}state {{\n    world {{\n        slot flag = 0\n    }}\n}}\n\nrule {PuckStrings.Write(value: name)} {{\n    flag = 1\n}}\n"),
        new(Label: "stabilize member", Source: static name => $"{WorldSources.Header}state {{\n    world {{\n        slot flag = 0\n    }}\n}}\n\nstabilize settle maxPasses(4) {{\n    rule {PuckStrings.Write(value: name)} {{\n        flag = 0\n    }}\n}}\n"),
        new(Label: "set", Source: static name => $"{WorldSources.Header}state {{\n    world {{\n        table probe {{\n            a = 1\n        }}\n    }}\n}}\n\nset {PuckStrings.Write(value: name)}: board(probe, 0..1)\n"),
        new(Label: "placement id", Source: static name => $"{WorldSources.Header}placements {{\n    rows [\n        {{\n            id: {PuckStrings.Write(value: name)}\n            prototypeId: \"p\"\n        }}\n    ]\n}}\n"),
        new(Label: "destination name", Source: static name => $"{WorldSources.Header}destinations [\n    {{\n        name: {PuckStrings.Write(value: name)}\n        reference: \"r\"\n        durability: \"persisted\"\n    }}\n]\n"),
        new(Label: "reference name", Source: static name => $"{WorldSources.Header}references [\n    {{\n        name: {PuckStrings.Write(value: name)}\n        document: \"d\"\n    }}\n]\n"),
        new(Label: "prototype id", Source: static name => $"{WorldSources.Header}prototypes [\n    {{\n        id: {PuckStrings.Write(value: name)}\n    }}\n]\n"),
        new(File: true, Label: "world name", Multiple: true, Source: static name => $"module m() {{\n    state {{\n        world {{\n            slot hp = 1\n        }}\n    }}\n}}\n\nworld {PuckStrings.Write(value: name)} = m()\n"),
        new(File: true, Label: "source file name", Path: static name => $"{name}.puck", Source: static _ => $"{WorldSources.Header}state {{\n    world {{\n        slot hp = 1\n    }}\n}}\n\ntest \"reads\" {{\n    expect {{\n        hp == 1\n    }}\n}}\n"),
    ];
    // The names a door is asked about: every corpus name in the reserved form that the position could otherwise
    // hold, and each one's author-writable twin with the reserved character replaced.
    private static readonly IReadOnlyList<string> DocumentCorpus = [
        .. IdentifierCorpus.Names(count: 400).Where(predicate: static name => (
            GeneratedName.IsGenerated(name: name) &&
            !name.StartsWith(value: IdentifierSpelling.Sigil) &&
            CellName.TryParse(candidate: name, name: out _, reason: out _)
        )),
    ];
    private static readonly IReadOnlyList<string> FileCorpus = ["a~b", "rulepush~push-block", "a~", "west~east~north"];

    private static DiagnosticBag Diagnose(Door door, string name) => WorldCompiler.Compile(
        allowMultiple: door.Multiple,
        cancellationToken: TestContext.Current.CancellationToken,
        defaultSchema: "puck.world.definition.v1",
        imports: ImportHandling.Ignore,
        source: door.Source(arg: name),
        sourcePath: door.Path?.Invoke(arg: name)
    ).Diagnostics;
    private static bool RefusesAsReserved(DiagnosticBag diagnostics) => diagnostics.Any(predicate: static diagnostic => (
        (diagnostic.Severity == DiagnosticSeverity.Error) &&
        (diagnostic.Code == PuckDiagnosticCodes.GeneratedNameReserved) &&
        diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "reserves")
    ));
    private static List<string> DoorViolations(Door door) {
        var violations = new List<string>();
        var joiner = (door.File ? GeneratedName.FileJoiner : GeneratedName.Joiner);

        foreach (var name in (door.File ? FileCorpus : DocumentCorpus)) {
            if (!RefusesAsReserved(diagnostics: Diagnose(door: door, name: name))) {
                violations.Add(item: $"{door.Label}: '{name}' is in the reserved form and is not refused as reserved");
            }

            var twin = name.Replace(newChar: '-', oldChar: joiner);

            if (RefusesAsReserved(diagnostics: Diagnose(door: door, name: twin))) {
                violations.Add(item: $"{door.Label}: '{twin}' carries no reserved character and is refused as reserved");
            }
            if (door.File) {
                continue;
            }

            // A document name may not carry the file joiner either: it is the character the name's own directory
            // spelling uses (GeneratedName.ToFile).
            var fileSpelled = name.Replace(newChar: GeneratedName.FileJoiner, oldChar: GeneratedName.Joiner);

            if (!RefusesAsReserved(diagnostics: Diagnose(door: door, name: fileSpelled))) {
                violations.Add(item: $"{door.Label}: '{fileSpelled}' carries '{GeneratedName.FileJoiner}' and is not refused as reserved");
            }
        }

        return violations;
    }

    public static TheoryData<string> DoorLabels() => [.. Doors.Select(selector: static door => door.Label)];
    [MemberData(memberName: nameof(DoorLabels))]
    [Theory]
    public void EveryAuthorDoorRefusesTheReservedFormBareOrQuoted(string label) {
        Assert.NotEmpty(collection: DocumentCorpus);
        AssertNone(violations: DoorViolations(door: Doors.Single(predicate: door => (door.Label == label))));
    }
    // The mutation proof: a position no door guards — an extension's free member, which the document carries as the
    // author wrote it — holds a generated name without refusal, and the law names it.
    [Fact]
    public void TheDoorLawCatchesAPositionNoDoorGuards() => Assert.NotEmpty(collection: DoorViolations(door: new Door(
        Label: "extension member",
        Source: static name => $"{WorldSources.Header}extensions {{\n    probeExt {{\n        seat: {PuckStrings.Write(value: name)}\n    }}\n}}\n"
    )));
}
