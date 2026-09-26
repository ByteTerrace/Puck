using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.State;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The decompiler's side of generated names: every site that mints one prints it back as the construct that
/// minted it, so decompiling a document and compiling the source reproduces the same document, and never prints a
/// generated name as an authored one. A site whose name no construct prints back (a name the engine seeds at run time,
/// or a hand-written document spelling a generated name) is refused by name. One row per minting site, each with the
/// smallest source or document that reaches it.</summary>
/// <remarks>The table is held to the tree: the minting calls (<see cref="GeneratedName"/>'s <c>Join</c>, <c>Append</c>,
/// <c>Qualify</c>, <c>JoinFile</c> and <c>AppendFile</c>) are counted per source file under <c>src/</c>, and a file whose count
/// differs from what <see cref="Sites"/> declares — a new minting site, or one retired — fails
/// <see cref="EveryMintingCallIsASiteOfTheTable"/> by name, as does a site no row reads back.</remarks>
public sealed class GeneratedNameReversalLawTests {
    // ---- the minting sites ----------------------------------------------------------------------------------------

    // One file of src/ that mints generated names: how many minting calls it holds and what they mint.
    private sealed record Site(string File, int Calls, string Mints);

    private const string Captures = "src/Puck.World.Schema/WorldCaptures.cs";
    private const string Catalog = "src/Puck.State/StateCatalog.cs";
    private const string Endpoints = "src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.CompositionEndpoints.cs";
    private const string Expansion = "src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.cs";
    private const string Extensions = "src/Puck.World.Server/WorldConfiguredExtensions.Hosting.cs";
    private const string Identity = "src/Puck.World.Schema/WorldIdentityRows.cs";
    private const string Links = "src/Puck.World.Transpiler/Lowering/WorldCompositionLinks.cs";
    private const string ModuleInstances = "src/Puck.Transpiler/Lowering/DocumentScope.ModuleInstances.cs";
    private const string ModuleNamespace = "src/Puck.World.Schema/WorldModuleNamespace.cs";
    private const string Probes = "src/Puck.World/WorldProbes.cs";
    private const string Sessions = "src/Puck.World.Schema/WorldSessionResolver.cs";
    private const string Stabilize = "src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.Stabilize.cs";
    private const string Tests = "src/Puck.World.Transpiler/Lowering/WorldDocumentEmitter.Tests.cs";
    private const string Views = "src/Puck.World.Client/WorldViewNames.cs";

    private static readonly IReadOnlyList<Site> Sites = [
        new(Calls: 1, File: Stabilize, Mints: "a rule scope's name joined to every rule, scope, stabilize group and workflow it holds, and a group's to its members"),
        new(Calls: 2, File: Endpoints, Mints: "a ground block's prototype, and the ground an aliased module instance declares"),
        new(Calls: 2, File: ModuleNamespace, Mints: "every name an aliased module instance declares, and its machines' own local names"),
        new(Calls: 1, File: ModuleInstances, Mints: "a qualified reference to a name an aliased module instance declares"),
        new(Calls: 2, File: Links, Mints: "a door's return arch, and a border's or door's reference and destination"),
        new(Calls: 1, File: Expansion, Mints: "a module row argument's placeholder"),
        new(Calls: 5, File: Tests, Mints: "a test's recorded keyed read, witness row, world, sibling world, and verdict row and rule"),
        new(Calls: 2, File: Catalog, Mints: "a pool's live, generation and field rows"),
        new(Calls: 7, File: Identity, Mints: "an owned identity's rate, chat, controller and sequence rows"),
        new(Calls: 1, File: Sessions, Mints: "a freshly started instance's name"),
        new(Calls: 1, File: Extensions, Mints: "an extension runtime's directory"),
        new(Calls: 1, File: Captures, Mints: "a capture's frame file"),
        new(Calls: 3, File: Views, Mints: "a session screen's and a camera seat's view name, and the synthesized root graph's own versions and passes, which exist only at run time"),
        new(Calls: 1, File: Probes, Mints: "a seat-relative probe's instance key, which exists only at run time"),
    ];
    private static readonly Regex MintingCall = new(
        options: RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        pattern: @"\bGeneratedName\s*\.\s*(?<method>Join|Append|Qualify|JoinFile|AppendFile)\s*\("
    );

    // Every minting call under src/, by file: the repository-relative forward-slashed path and the methods called.
    private static Dictionary<string, List<string>> ScanMintingCalls() {
        var root = RepositoryPaths.RequireRoot();
        var calls = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(path: Path.Combine(path1: root, path2: "src"), searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")) {
            var relative = Path.GetRelativePath(path: path, relativeTo: root).Replace(newChar: '/', oldChar: '\\');

            if (relative.Contains(comparisonType: StringComparison.Ordinal, value: "/bin/") || relative.Contains(comparisonType: StringComparison.Ordinal, value: "/obj/")) {
                continue;
            }

            var matches = MintingCall.Matches(input: File.ReadAllText(path: path));

            if (matches.Count > 0) {
                calls[relative] = [.. matches.Select(selector: static match => match.Groups["method"].Value)];
            }
        }

        return calls;
    }
    private static List<string> SiteViolations(Dictionary<string, List<string>> scanned, IReadOnlyList<Site> sites) {
        var violations = new List<string>();

        foreach (var (file, methods) in scanned) {
            var site = sites.SingleOrDefault(predicate: candidate => (candidate.File == file));

            if (site is null) {
                violations.Add(item: $"{file} mints {methods.Count} generated name(s) ({string.Join(separator: ", ", values: methods)}) and is no site of the table: add a site and a row that reads its names back");
            } else if (site.Calls != methods.Count) {
                violations.Add(item: $"{file} holds {methods.Count} minting call(s) and the table declares {site.Calls}: a site was added or retired, so its row must be too");
            }
        }
        foreach (var site in sites) {
            if (!scanned.ContainsKey(key: site.File)) {
                violations.Add(item: $"{site.File} mints nothing any more: retire its site and its rows");
            }
            if (!Rows.Any(predicate: row => (row.Site == site.File))) {
                violations.Add(item: $"{site.File} ({site.Mints}) has no row reading its names back");
            }
        }

        return violations;
    }

    [Fact]
    public void EveryMintingCallIsASiteOfTheTable() => AssertNone(violations: SiteViolations(scanned: ScanMintingCalls(), sites: Sites));
    // The mutation proof of the completeness check: a minting call in a file the table does not know, and a second
    // one in a file it does, each fail by name.
    [Fact]
    public void TheCompletenessCheckNamesANewMintingSite() {
        var scanned = ScanMintingCalls();

        scanned["src/Puck.World.Server/WorldNewMinter.cs"] = ["Join"];
        Assert.Contains(collection: SiteViolations(scanned: scanned, sites: Sites), filter: static violation => violation.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/Puck.World.Server/WorldNewMinter.cs"));

        scanned = ScanMintingCalls();
        scanned[Endpoints].Add(item: "Append");
        Assert.Contains(collection: SiteViolations(scanned: scanned, sites: Sites), filter: static violation => violation.StartsWith(comparisonType: StringComparison.Ordinal, value: Endpoints));
    }

    // ---- the rows -------------------------------------------------------------------------------------------------

    // One construct a site mints names for, and the check that reads them back.
    public sealed record Row(string Label, string Site, Func<List<string>> Check);

    private const string AliasedGround = """
        schema: "puck.world.definition.v1"
        documentId: "yards"

        module yard() {
            ground floor {
                size [12m, 8m]
            }
        }

        use yard as east()
        use yard as west()
        """;
    private const string ComposedTest = $$"""
        {{TestWorldFixtures.InlineArmoury}}

        entry world west = armoury(plating: 2)
        world east = armoury(plating: 3)

        test "a composed claim" {
            expect {
                west {
                    armour == 2
                }
                east {
                    armour == 3
                }
            }
        }
        """;
    private const string Composition = """
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
        module skyRoom() {
            spawn landing { at [0m, 40m, 0m] }
        }
        world island = islandRoom()
        world parlor = parlorRoom()
        world sky = skyRoom()
        door island.arch1, parlor.arrival
        border island.floor.east, parlor.floor.west { height: 6m hysteresis: 2m }
        border parlor.lookout, sky.under { center [12m, 20m, 0m] pitch: -90 width: 10m height: 10m }
        """;
    private const string Ground = """
        schema: "puck.world.definition.v1"
        documentId: "yard"

        ground floor {
            center [2m, 1.5m, -3m]
            size [24m, 12.5m]
        }

        ground terrace {
            size [4m, 4m]
        }

        prototypes {
            prototype "post" {
                document {
                    name: "post"
                    schema: "puck.creation.v1"
                }
            }
        }
        """;
    private const string Groups = """
        schema: "puck.world.definition.v1"
        documentId: "groups"

        state {
            world {
                slot flag = 0
            }
        }

        stabilize settle maxPasses(4) {
            rule collapse {
                when flag == 1
                flag = 0
            }
        }

        workflow turn {
            step begin {
                flag = 1
            }

            step finish skip {
                flag = 2
            }
        }

        rules outer {
            stabilize inner maxPasses(2) {
                rule calm {
                    when flag == 2
                    flag = 0
                }
            }
        }
        """;
    private const string InterleavedGround = """
        schema: "puck.world.definition.v1"
        documentId: "yard"

        prototypes {
            prototype "post" {
                document {
                    name: "post"
                    schema: "puck.creation.v1"
                }
            }
        }

        placements {
            placement "gate" {
                prototype: "post"
                position [1, 0, 1]
            }
        }

        ground floor {
            size [8m, 8m]
        }

        placements {
            placement "fence" {
                prototype: "post"
                position [2, 0, 2]
            }
        }

        ground lawn {
            center [0m, 0m, 20m]
            size [8m, 8m]
        }

        prototypes {
            prototype "rail" {
                document {
                    name: "rail"
                    schema: "puck.creation.v1"
                }
            }
        }
        """;
    private const string ModuleArgument = """
        schema: "puck.world.definition.v1"
        documentId: "guarded"

        state {
            world {
                slot enabled = true
            }
        }

        module guarded(condition: Gate) {
            state { world { slot fired = false } }
            rule fire {
                when condition == true
                fired = true
            }
        }
        use guarded(condition: enabled)
        """;
    private const string QualifiedReference = """
        schema: "puck.world.definition.v1"
        documentId: "counters"

        module counter() {
            state { world { slot score = 0 } }
            export read score
            export action score
        }

        use counter as left()

        rule bump {
            when left.score < 3
            left.score = left.score + 1
        }
        """;
    private const string ScopeRules = """
        schema: "puck.world.definition.v1"
        documentId: "scopes"

        state {
            world {
                slot flag = 0
                slot steps = 0
            }
        }

        rules dealing when flag == 0 {
            rule start {
                flag = 1
            }
        }

        rules outer {
            rules inner {
                rule deep {
                    when steps < 3
                    steps = steps + 1
                }
            }
        }
        """;
    private const string SubjectTest = """
        schema: "puck.world.definition.v1"
        documentId: "tower"

        module tower(height) {
            state {
                world {
                    slot floors = height
                }
            }
        }

        test "a tower stands as tall as it is built" with tower(height: 4) {
            expect {
                floors == 4
            }
        }
        """;
    private const string TestBlock = """
        schema: "puck.world.definition.v1"
        documentId: "kinds"

        state {
            world {
                slot hp = 3
                slot speed = 1.5
                slot ready = false
                table board {
                    a = 1
                }
            }
        }

        rule "heal" {
            when hp < 5
            hp = hp + 1
        }

        test "kinds" {
            given {
                speed = 2.25
            }
            when {
                ticks 2
                seat1: world.state.cell.set hp $value 1
                ticks 3
                seat1: world.state.cell.set hp $value 2
                ticks 9
            }
            expect {
                speed > 2.0 and hp == 3
                board[a] == 1
                ready == false
            }
        }
        """;

    public static IReadOnlyList<Row> Rows { get; } = [
        new(Label: "rule held by a scope", Site: Stabilize, Check: static () => RoundTrip(source: ScopeRules)),
        new(Label: "stabilize group and members, workflow and steps, group held by a scope", Site: Stabilize, Check: static () => RoundTrip(source: Groups)),
        new(Label: "ground prototype", Site: Endpoints, Check: static () => RoundTrip(source: Ground)),
        new(Label: "ground beside a border, door return arch, link reference and destination", Site: Links, Check: static () => CompositionRoundTrip(source: Composition)),
        new(Label: "ground written between other prototypes and placements", Site: Endpoints, Check: static () => RoundTrip(source: InterleavedGround)),
        new(Label: "ground of two aliased module instances", Site: Endpoints, Check: static () => RefusedSource(name: "east$ground$floor", source: AliasedGround)),
        new(Label: "row an aliased module instance declares", Site: ModuleNamespace, Check: static () => RefusedSource(name: "left$score", source: QualifiedReference)),
        new(Label: "qualified reference to an aliased instance's row", Site: ModuleInstances, Check: static () => RefusedSource(name: "left$score", source: QualifiedReference)),
        new(Label: "ground prototype a document reshaped", Site: Endpoints, Check: static () => RefusedAltered(alter: static document => document["prototypes"]![0]!["document"]!["palette"]![0]!["color"] = "#FF0000", name: "ground$floor", source: Ground)),
        new(Label: "link rows of one world read alone", Site: Links, Check: static () => RefusedWorld(name: "link$arch1", source: Composition, world: "island")),
        new(Label: "return arch of one world read alone", Site: Links, Check: static () => RefusedWorld(name: "return$island$arch1", source: Composition, world: "parlor")),
        new(Label: "module row argument placeholder", Site: Expansion, Check: static () => [.. RoundTrip(source: ModuleArgument), .. NeverDeclared(head: "arg", source: ModuleArgument)]),
        new(Label: "module row argument placeholder a hand document spells", Site: Expansion, Check: static () => RefusedDocument(document: HandDocument(row: GeneratedName.Join("arg", "0")), name: GeneratedName.Join("arg", "0"))),
        new(Label: "verdict row and rule, witness rows, recorded keys, status key, test world", Site: Tests, Check: static () => TestRoundTrip(source: TestBlock)),
        new(Label: "test world standing up a module", Site: Tests, Check: static () => TestRoundTrip(source: SubjectTest)),
        new(Label: "test world and sibling world names stay files", Site: Tests, Check: static () => FileBackedTestWorlds(source: ComposedTest)),
        new(Label: "composed test's boot document", Site: Tests, Check: static () => RefusedComposedTest(source: ComposedTest)),
        new(Label: "pool rows", Site: Catalog, Check: static () => RefusedDocument(document: HandDocument(row: GeneratedName.Join("$pool", "pieces", "live")), name: GeneratedName.Join("$pool", "pieces", "live"))),
        new(Label: "identity rows", Site: Identity, Check: static () => RefusedDocument(document: HandDocument(row: WorldIdentityRows.MoveSpeed.Value), name: WorldIdentityRows.MoveSpeed.Value)),
        new(Label: "fresh instance name", Site: Sessions, Check: static () => FileBacked(site: Sessions)),
        new(Label: "extension directory", Site: Extensions, Check: static () => FileBacked(site: Extensions)),
        new(Label: "capture frame file", Site: Captures, Check: static () => FileBacked(site: Captures)),
        new(Label: "view name a hand document spells", Site: Views, Check: static () => RefusedDocument(document: HandDocument(row: GeneratedName.Join("session", "0")), name: GeneratedName.Join("session", "0"))),        new(Label: "probe instance key a hand document spells", Site: Probes, Check: static () => RefusedDocument(document: HandDocument(row: GeneratedName.Join("head", "2")), name: GeneratedName.Join("head", "2"))),
    ];

    public static TheoryData<string> RowLabels() => [.. Rows.Select(selector: static row => row.Label)];
    [MemberData(memberName: nameof(RowLabels))]
    [Theory]
    public void EveryGeneratedNameReadsBackAsItsConstructOrIsRefusedByName(string label) =>
        AssertNone(violations: Rows.Single(predicate: row => (row.Label == label)).Check());

    // ---- the checks -----------------------------------------------------------------------------------------------

    private static WorldCompilation Compile(string source, List<string> violations, string label, bool allowMultiple = false) {
        var compilation = WorldCompiler.Compile(
            allowMultiple: allowMultiple,
            cancellationToken: TestContext.Current.CancellationToken,
            defaultSchema: "puck.world.definition.v1",
            imports: ImportHandling.Ignore,
            source: source,
            sourcePath: Path.Combine(path1: Path.GetTempPath(), path2: "reversal.puck")
        );

        if (compilation.Diagnostics.HasErrors) {
            violations.Add(item: $"{label} does not compile:{Environment.NewLine}{compilation.Diagnostics.FormatReport(source)}{Environment.NewLine}{source}");
        }

        return compilation;
    }
    private static string Canonical(JsonNode node) => DocumentLowering.Canonicalize(node: node.DeepClone())!.ToJsonString();
    private static void Same(JsonObject? expected, JsonObject? actual, string label, List<string> violations) {
        if ((expected is null) || (actual is null)) {
            violations.Add(item: $"{label}: a document is missing");
        } else if (Canonical(node: expected) != Canonical(node: actual)) {
            violations.Add(item: $"{label}: the recompiled document differs{Environment.NewLine}expected {Canonical(node: expected)}{Environment.NewLine}actual   {Canonical(node: actual)}");
        }
    }
    private static string? Decompile(Func<string> decompile, List<string> violations, string label) {
        try {
            return decompile();
        } catch (WorldDecompileRefusedException refusal) {
            violations.Add(item: $"{label}: the decompiler refused it: {refusal.Message}");

            return null;
        }
    }
    private static List<string> RoundTrip(string source) {
        var violations = new List<string>();
        var compiled = Compile(label: "the source", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var document = compiled.RequireJson();

        if (Decompile(decompile: () => WorldDecompiler.Decompile(root: document), label: "the document", violations: violations) is not { } decompiled) {
            return violations;
        }

        var recompiled = Compile(label: "the decompiled source", source: decompiled, violations: violations);

        if (violations.Count == 0) {
            Same(actual: recompiled.RequireJson(), expected: document, label: "the round trip", violations: violations);
        }

        return violations;
    }
    private static List<string> CompositionRoundTrip(string source) {
        var violations = new List<string>();
        var compiled = Compile(allowMultiple: true, label: "the composition", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var worlds = compiled.Worlds.Select(selector: static output => KeyValuePair.Create(key: output.Name, value: output.Json)).ToList();

        if (Decompile(decompile: () => WorldDecompiler.DecompileComposition(worlds: worlds), label: "the composition's worlds", violations: violations) is not { } decompiled) {
            return violations;
        }

        var recompiled = Compile(allowMultiple: true, label: "the decompiled composition", source: decompiled, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        foreach (var output in compiled.Worlds) {
            Same(
                actual: recompiled.Worlds.SingleOrDefault(predicate: candidate => (candidate.Name == output.Name))?.Json,
                expected: output.Json,
                label: $"world '{output.Name}'",
                violations: violations
            );
        }

        if (recompiled.Worlds.Count != compiled.Worlds.Count) {
            violations.Add(item: $"the decompiled composition emits {recompiled.Worlds.Count} worlds, not {compiled.Worlds.Count}");
        }

        return violations;
    }
    private static List<string> TestRoundTrip(string source) {
        var violations = new List<string>();
        var compiled = Compile(label: "the source", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var world = Assert.Single(collection: compiled.TestWorlds).Json;

        if (Decompile(decompile: () => WorldDecompiler.Decompile(root: world), label: "the test world", violations: violations) is not { } decompiled) {
            return violations;
        }

        var recompiled = Compile(label: "the decompiled test world", source: decompiled, violations: violations);

        if (violations.Count == 0) {
            Same(actual: recompiled.TestWorlds.SingleOrDefault()?.Json, expected: world, label: "the test world's round trip", violations: violations);
        }

        return violations;
    }
    // A name the lowering stands in and replaces never reaches the document.
    private static List<string> NeverDeclared(string source, string head) {
        var violations = new List<string>();
        var compiled = Compile(label: "the source", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var text = compiled.RequireJson().ToJsonString();
        var needle = $"\"{head}{GeneratedName.Joiner}";

        if (text.Contains(comparisonType: StringComparison.Ordinal, value: needle)) {
            violations.Add(item: $"the document carries a name opening '{head}{GeneratedName.Joiner}', which the lowering stands in and replaces");
        }

        return violations;
    }
    private static JsonObject HandDocument(string row) => new() {
        ["schema"] = "puck.world.definition.v1",
        ["documentId"] = "hand",
        ["state"] = new JsonObject {
            ["world"] = new JsonArray(new JsonObject { ["kind"] = "Int", ["name"] = row, ["value"] = 0 }),
        },
    };
    private static List<string> RefusedDocument(JsonObject document, string name) {
        try {
            var printed = WorldDecompiler.Decompile(root: document);

            return [$"the decompiler printed '{name}' instead of refusing it:{Environment.NewLine}{printed}"];
        } catch (WorldDecompileRefusedException refusal) when ((refusal.Name == name)) {
            return [];
        } catch (WorldDecompileRefusedException refusal) {
            return [$"the decompiler refused '{refusal.Name}', not '{name}': {refusal.Message}"];
        }
    }
    private static List<string> RefusedSource(string source, string name) {
        var violations = new List<string>();
        var compiled = Compile(label: "the source", source: source, violations: violations);

        return ((violations.Count > 0)
            ? violations
            : RefusedDocument(document: compiled.RequireJson(), name: name)
        );
    }
    private static List<string> RefusedAltered(string source, Action<JsonObject> alter, string name) {
        var violations = new List<string>();
        var compiled = Compile(label: "the source", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var document = ((JsonObject)compiled.RequireJson().DeepClone());

        alter(obj: document);

        return RefusedDocument(document: document, name: name);
    }
    private static List<string> RefusedWorld(string source, string world, string name) {
        var violations = new List<string>();
        var compiled = Compile(allowMultiple: true, label: "the composition", source: source, violations: violations);

        return ((violations.Count > 0)
            ? violations
            : RefusedDocument(document: compiled.Worlds.Single(predicate: output => (output.Name == world)).Json, name: name)
        );
    }
    private static List<string> RefusedComposedTest(string source) {
        var violations = new List<string>();
        var compiled = Compile(allowMultiple: true, label: "the composition", source: source, violations: violations);

        if (violations.Count > 0) {
            return violations;
        }

        var boot = compiled.TestWorlds.Single(predicate: static world => (world.Siblings.Count > 0)).Json;

        return RefusedDocument(document: boot, name: GeneratedName.Join("expect", "1"));
    }
    // A test's world and its siblings are files: each name is in the file form, and no document of the set declares a
    // name carrying the file joiner.
    private static List<string> FileBackedTestWorlds(string source) {
        var violations = new List<string>();
        var compiled = Compile(allowMultiple: true, label: "the composition", source: source, violations: violations);

        foreach (var world in compiled.TestWorlds) {
            foreach (var (name, json) in new[] { (world.Name, world.Json) }.Concat(second: world.Siblings.Select(selector: static sibling => (sibling.Name, sibling.Json)))) {
                if (!GeneratedName.IsGeneratedFile(name: name)) {
                    violations.Add(item: $"test world '{name}' is not in the file form");
                }

            }
        }
        if (compiled.TestWorlds.Count == 0) {
            violations.Add(item: "the composition generated no test world, so the law saw nothing");
        }

        return violations;
    }
    // A site that mints only file-backed names writes nothing a document declares.
    private static List<string> FileBacked(string site) {
        var methods = (ScanMintingCalls().GetValueOrDefault(key: site) ?? []);

        return [.. methods
            .Where(predicate: static method => (method is not ("JoinFile" or "AppendFile")))
            .Select(selector: method => $"{site} mints with {method}, a document name, and its row reads it back as a file name")];
    }
    private static void AssertNone(List<string> violations) => Assert.True(
        condition: (violations.Count == 0),
        userMessage: string.Join(separator: Environment.NewLine, values: violations)
    );
}
