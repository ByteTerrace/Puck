using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

/// <summary>One document of a generated test beside the one the run boots with.</summary>
/// <param name="Name">The document's own name, which is the stem a <c>--keep</c> run writes it under and the name
/// the boot document's <c>schedule.instances</c> arms it by.</param>
/// <param name="Json">The canonical generated document.</param>
public sealed record WorldTestSibling(string Name, JsonObject Json);
/// <summary>One generated test world: the enclosing world's own document plus what a <c>test</c> block asked
/// for.</summary>
/// <param name="Name">The world's own generated name (<see cref="GeneratedName.JoinFile"/>): the enclosing source's
/// stem, the instance a module's test was brought by, and the test's slug, joined by <c>~</c> — the name
/// <c>puck test</c> reports a verdict under and the stem of the document a <c>--keep</c> run writes.</param>
/// <param name="Test">The test's authored name.</param>
/// <param name="Json">The canonical generated document the run boots with.</param>
/// <param name="Subject">The module invocation this world stands up, as it was written, or <see langword="null"/>
/// when the world is the document the test was written in.</param>
public sealed record WorldTestWorld(string Name, string Test, JsonObject Json, string? Subject = null) {
    /// <summary>Gets the documents this test needs on disk beside <see cref="Json"/>: one per other world of the
    /// composition it stands at the root of, armed by the boot document's own <c>schedule.instances</c>. Empty for a
    /// test over one world.</summary>
    public IReadOnlyList<WorldTestSibling> Siblings { get; init; } = [];
    /// <summary>Gets the expectations the test addresses to a sibling document by name (<c>garden { … }</c>), in the
    /// order written. The document is not part of this test's world: a crossing starts it, so each expectation is
    /// lowered into that document's staged copy with <see cref="WorldTestForeign.TryApply"/>. Empty for a test that
    /// addresses none.</summary>
    public IReadOnlyList<WorldTestForeign> Foreign { get; init; } = [];
}
/// <summary>One expectation of a test about a sibling document: a claim judged in the document a crossing starts,
/// rather than in the world the run boots.</summary>
/// <param name="Document">The document the expectation names.</param>
/// <param name="Line">The 1-based line of the test's <c>expect</c> block, which names the verdict row
/// (<c>expect$&lt;line&gt;</c>).</param>
/// <param name="Gate">The gate as written, which the verdict reports.</param>
/// <param name="Reads">Every row and key the gate reads.</param>
/// <param name="Rule">The rule that writes the verdict whenever the gate holds, lowered and without the effects that
/// copy what the gate read into the verdict's rows.</param>
public sealed record WorldTestForeign(string Document, int Line, string Gate, IReadOnlyList<(string State, string? Key)> Reads, JsonObject Rule) {
    /// <summary>Returns the expectations as one JSON array, the form a compile cache holds them in.</summary>
    /// <param name="expectations">The expectations.</param>
    /// <returns>The JSON text; <see cref="FromJson"/> reads it back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectations"/> is <see langword="null"/>.</exception>
    public static string ToJson(IReadOnlyList<WorldTestForeign> expectations) {
        ArgumentNullException.ThrowIfNull(argument: expectations);

        var array = new JsonArray();

        foreach (var expectation in expectations) {
            array.AppendNode(item: new JsonObject {
                ["document"] = expectation.Document,
                ["gate"] = expectation.Gate,
                ["line"] = expectation.Line,
                ["reads"] = new JsonArray([.. expectation.Reads.Select(selector: static read => (JsonNode?)new JsonObject {
                    ["key"] = read.Key,
                    ["state"] = read.State,
                })]),
                ["rule"] = expectation.Rule.DeepClone(),
            });
        }

        return array.ToJsonString();
    }
    /// <summary>Reads expectations written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The expectations, in the order written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException"><paramref name="json"/> is not text <see cref="ToJson"/>
    /// wrote.</exception>
    public static IReadOnlyList<WorldTestForeign> FromJson(string json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        var expectations = new List<WorldTestForeign>();

        foreach (var node in JsonNode.Parse(json: json)!.AsArray()) {
            var entry = node!.AsObject();

            expectations.Add(item: new WorldTestForeign(
                Document: entry["document"]!.GetValue<string>(),
                Gate: entry["gate"]!.GetValue<string>(),
                Line: entry["line"]!.GetValue<int>(),
                Reads: [.. entry["reads"]!.AsArray().Select(selector: static read => (read!["state"]!.GetValue<string>(), read["key"]?.GetValue<string>()))],
                Rule: (JsonObject)entry["rule"]!.DeepClone()
            ));
        }

        return expectations;
    }
    /// <summary>Gets the name of the verdict row this expectation declares in its document.</summary>
    public string Row => WorldDocumentEmitter.VerdictRow(line: Line);

    /// <summary>Adds this expectation to a sibling document: its verdict row, the witness rows for what the gate reads
    /// of rows that are not Int, and the rule that passes the verdict on every tick the claim holds, so a document
    /// where it never holds reports the verdict as never evaluated.</summary>
    /// <param name="world">The sibling document as it compiles, which this call adds rows and a rule to.</param>
    /// <param name="reason">The refusal when the document already carries the verdict's name, otherwise
    /// <see langword="null"/>.</param>
    /// <param name="inheritedState">The composed state section the sibling document inherits through its
    /// <c>basis</c> and <c>imports</c>, when it has any.</param>
    /// <returns><see langword="true"/> when the expectation was added.</returns>
    public bool TryApply(JsonObject world, out string? reason, JsonObject? inheritedState = null) {
        ArgumentNullException.ThrowIfNull(argument: world);

        if (world["state"] is not JsonObject state) {
            state = [];
            world["state"] = state;
        }

        if (state["world"] is not JsonArray rows) {
            rows = [];
            state["world"] = rows;
        }

        if (world["rules"] is not JsonArray rules) {
            rules = [];
            world["rules"] = rules;
        }

        var row = Row;

        if (
            rules.OfType<JsonObject>().Any(predicate: candidate => (candidate["name"]?.GetValue<string>() == row)) ||
            rows.OfType<JsonObject>().Any(predicate: candidate => (candidate["name"]?.GetValue<string>() == row))
        ) {
            reason = $"the document '{Document}' already declares '{row}'";

            return false;
        }

        var rule = (JsonObject)Rule.DeepClone();

        WorldDocumentEmitter.AddVerdictRows(
            effects: rule["effects"]!.AsArray(),
            gate: Gate,
            inheritedState: inheritedState,
            reads: Reads,
            row: row,
            rows: rows,
            state: state
        );
        rules.AppendNode(item: rule);
        reason = null;

        return true;
    }
}
// A `test` block reaches no member of the document it is written in: it is collected while that document lowers
// and each collected test is lowered afterwards against the finished document, so a world compiled without
// `puck test` is byte-identical to the same source with its tests deleted.
public static partial class WorldDocumentEmitter {
    /// <summary>One <c>test</c> block waiting to be lowered, with the scope it was written in.</summary>
    /// <param name="Declaration">The block as it was written, with a module subject when the test has one.</param>
    /// <param name="Scope">The scope the block's names, constants and subject arguments resolve in.</param>
    /// <param name="Name">The name the generated world and its verdict rows are addressed by, which carries the
    /// instance for a test a module brought with it.</param>
    /// <param name="Instance">The spelling of the instantiation that brought a module's own test here, or
    /// <see langword="null"/> for a test written at a document's root. Two instantiations sharing one spelling run
    /// the test once.</param>
    /// <param name="Qualifiers">The parts the generated world's name carries between the source's stem and the
    /// test's slug: each instance that brought the test, outermost first, with its ordinal when one instance label
    /// brought it twice. Empty for a test written at a document's root.</param>
    private sealed record CollectedTest(TestDeclarationNode Declaration, DocumentScope Scope, string Name, string? Instance = null, IReadOnlyList<string>? Qualifiers = null);

    /// <summary>One world of a composition, as a subjectless <c>test</c> at that composition's root reaches it.</summary>
    /// <param name="Name">The world's own name, which a test's world block addresses it by.</param>
    /// <param name="Json">The world's finished document.</param>
    /// <param name="Scope">The scope that world's own rows, constants and module arguments resolve in.</param>
    /// <param name="Entry">Whether the world is the composition's declared entry, the one a composed run boots.</param>
    internal sealed record TestWorldSubject(string Name, JsonObject Json, DocumentScope Scope, bool Entry = false);

    /// <summary>One document a test generates.</summary>
    /// <param name="World">The composition world this document is, or <see langword="null"/> when the test is about
    /// one world and nothing addresses it by name.</param>
    /// <param name="Document">The generated document's own name.</param>
    /// <param name="Json">The document being built.</param>
    /// <param name="Scope">The scope this document's own rows and constants resolve in.</param>
    /// <param name="InheritedState">The composed state section the document inherits through its <c>basis</c> and
    /// <c>imports</c>, when it has any.</param>
    private sealed record TestTarget(string? World, string Document, JsonObject Json, DocumentScope Scope, JsonObject? InheritedState = null);

    private const string InheritedStateAnnotation = "WorldInheritedState";
    private const string TestsAnnotation = "WorldTests";

    // The status cell of a generated verdict row: a reserved key the verdict row mints, so no value the expectation
    // read — every other cell but the engine's own `$firedTick` stamp — can be recorded under it.
    internal const string VerdictStatusKey = "$status";
    // The first part of every row and rule a test's expectations generate: `expect$<line>` and its witnesses.
    internal const string ExpectHead = "expect";
    // The floor on the margin between a test's last scheduled step and its export tick. A step is SUBMITTED at its
    // tick; the host's command pump drains it, the simulation lane applies it, and the run records the answer the
    // reconciliation reads — each on a later tick. A margin below this floor reaches the export with a row still
    // awaiting its answer, which the reconciliation refuses.
    internal const ulong MinimumSettleTicks = 6UL;

    private static List<CollectedTest> GetOrCreateTests(DocumentScope scope) {
        if (
            !scope.Annotations.TryGetValue(
            key: TestsAnnotation,
            value: out var held
        ) ||
            (held is not List<CollectedTest> tests)
        ) {
            tests = [];
            scope.Annotations[TestsAnnotation] = tests;
        }

        return tests;
    }

    /// <summary>Collects a <c>test</c> block written at the root of the document or module body being lowered.</summary>
    /// <param name="scope">The scope the block was written in.</param>
    /// <param name="test">The block as it was written.</param>
    internal static void CollectTest(TestDeclarationNode test, DocumentScope scope) => GetOrCreateTests(scope: scope).Add(item: new CollectedTest(
        Declaration: test,
        Name: test.Name,
        Scope: scope
    ));

    // A module brings its tests to every instantiation: each one is re-stated as a test of that instantiation's own
    // module and arguments, so it runs against what this source asked for rather than against the module's
    // defaults. Two instantiations that read identically run it once, and an instance that already carries a test
    // of the same name takes an ordinal so each generated world keeps its own name.
    // A world declaration's own name already heads every world its tests generate, so it brings them unqualified.
    private static void HoistModuleTests(CallExpressionNode call, DocumentScope scope, DocumentScope? invocation, string instance, bool qualify = true) {
        if (scope.Annotations.GetValueOrDefault(key: "IncludeTests") is false) { return; }
        if (invocation is null) {
            return;
        }

        var brought = GetOrCreateTests(scope: invocation);

        if (brought.Count == 0) {
            return;
        }

        var spelling = PuckPrinter.PrintExpression(expression: call);
        var tests = GetOrCreateTests(scope: scope);

        foreach (var test in brought) {
            // A test the module brought from a module of its own already names its own subject and carries the
            // scope that subject's arguments resolve in; this instantiation only prefixes its identity, so that two
            // instantiations of the outer module are told apart even where the inner call reads identically.
            var subjected = (test.Declaration.Subject is not null);
            var key = $"{spelling}::{(test.Instance ?? test.Declaration.Name)}";

            if (tests.Any(predicate: candidate => (candidate.Instance == key))) {
                continue;
            }

            var name = $"{instance} {test.Name}";
            var ordinal = 1;

            while (tests.Any(predicate: candidate => (candidate.Name == name))) {
                ordinal++;
                name = $"{instance} {ordinal.ToString(provider: CultureInfo.InvariantCulture)} {test.Name}";
            }

            List<string> qualifiers = (qualify
                ? [Slug(text: instance)]
                : []);

            if (ordinal > 1) {
                qualifiers.Add(item: ordinal.ToString(provider: CultureInfo.InvariantCulture));
            }

            qualifiers.AddRange(collection: (test.Qualifiers ?? []));
            tests.Add(item: new CollectedTest(
                Declaration: (subjected
                ? test.Declaration
                : (test.Declaration with { Subject = call })),
                Instance: key,
                Name: name,
                Qualifiers: qualifiers,
                Scope: (subjected
                ? test.Scope
                : scope)
            ));
        }
        brought.Clear();
    }
    // The part of a generated world's name a test's own name contributes: the authored text reduced to lowercase words
    // joined by dashes, which never carries the `~` the parts are joined by.
    private static string Slug(string text) {
        var builder = new StringBuilder();
        var dashed = false;

        foreach (var character in text) {
            if (char.IsAsciiLetterOrDigit(c: character)) {
                _ = builder.Append(value: char.ToLowerInvariant(c: character));
                dashed = false;
            } else if (!dashed && (builder.Length != 0)) {
                _ = builder.Append(value: '-');
                dashed = true;
            }
        }

        var slug = builder.ToString().TrimEnd(trimChar: '-');

        return ((slug.Length == 0)
            ? "test"
            : slug
        );
    }
    // The cell key a folded value is recorded under: the row's own name for a slot, and the generated `row$key` for
    // a cell of a keyed row, so no two reads share a key and none shares the status key. A read the key cannot name —
    // a generated or reserved row, or a key decided when the rule fires — records nothing.
    private static string? SawKey(string state, string? key) {
        if (state.Contains(value: GeneratedName.Joiner)) {
            return null;
        }

        if ((key is null) || (key == StateRow.SlotKey.Value)) {
            return state;
        }

        return (key.Contains(value: GeneratedName.Joiner)
            ? null
            : GeneratedName.Join(state, key)
        );
    }
    // Every (row, key) a lowered gate reads. A `compareValue` node holds operand text rather than a row, so it
    // folds nothing: what it read is an expression, and the verdict row holds cells.
    private static void CollectGateReads(JsonNode? node, List<(string State, string? Key)> into) {
        switch (node) {
            case JsonObject obj:
                if (
                    (obj["$type"]?.GetValue<string>() == "compareState") &&
                    (obj["state"]?.GetValue<string>() is { } state)
                ) {
                    into.Add(item: (state, obj["key"]?.GetValue<string>()));

                    if (obj["comparandState"]?.GetValue<string>() is { } comparand) {
                        into.Add(item: (comparand, obj["comparandKey"]?.GetValue<string>()));
                    }
                }
                foreach (var (_, child) in obj) {
                    CollectGateReads(
                        into: into,
                        node: child
                    );
                }

                break;
            case JsonArray array:
                foreach (var item in array) {
                    CollectGateReads(
                        into: into,
                        node: item
                    );
                }

                break;
            default:
                break;
        }
    }
    // The kind of a declared row of any lane of the document's state section, or null for a name none declares.
    private static CellKind? KindOfRow(JsonObject state, string name) {
        foreach (var (_, lane) in state) {
            if (lane is not JsonArray rows) {
                continue;
            }

            foreach (var candidate in rows.OfType<JsonObject>()) {
                if (candidate["name"]?.GetValue<string>() == name) {
                    return (Enum.TryParse<CellKind>(
                        result: out var kind,
                        value: (candidate["kind"]?.GetValue<string>() ?? nameof(CellKind.Int))
                    )
                        ? kind
                        : null
                    );
                }
            }
        }

        return null;
    }
    // The value a witness cell is authored at before its verdict's rule fires, or null for a kind no gate reads.
    private static JsonNode? UnseenValue(CellKind kind) => kind switch {
        CellKind.Bool => JsonValue.Create(value: false),
        CellKind.Fixed => JsonValue.Create(value: "0"),
        CellKind.Text => JsonValue.Create(value: ""),
        _ => null,
    };

    // The verdict row and rule of a test's expectation on its 1-based line of the expect block: `expect$<line>`.
    internal static string VerdictRow(int line) => GeneratedName.Join(ExpectHead, line.ToString(provider: CultureInfo.InvariantCulture));
    // The row that holds what a verdict's gate saw of rows of one kind other than Int: `expect$<line>$<kind>`.
    internal static string WitnessRow(string verdict, CellKind kind) => GeneratedName.Append(
        name: verdict,
        part: kind.ToString().ToLowerInvariant()
    );

    private static JsonObject SetVerdictCell(string row, string key, long value) => new() {
        ["$type"] = "setState",
        ["key"] = key,
        ["state"] = row,
        ["value"] = value,
    };

    /// <summary>Lowers every <c>test</c> block collected while <paramref name="document"/> was lowered, each to its
    /// own generated test world.</summary>
    /// <param name="document">The finished document the tests were written in, which every generated world is a
    /// copy of.</param>
    /// <param name="scope">The lowering scope the tests were collected in.</param>
    /// <param name="stem">The enclosing source's own stem, which names each generated world.</param>
    /// <returns>The generated worlds, in the order their tests were written.</returns>
    internal static IReadOnlyList<WorldTestWorld> LowerTests(JsonObject document, DocumentScope scope, string stem) => LowerTests(
        composition: null,
        document: document,
        scope: scope,
        stem: stem
    );
    /// <summary>Lowers every <c>test</c> block collected at the root of a source that emits several worlds. A test
    /// naming a module stands that module up alone; a subjectless one becomes the composed run — one generated
    /// document per world, with the composition's entry world, the one a boot of the source starts in, carrying the
    /// schedule that arms the rest.</summary>
    /// <param name="worlds">The composition's finished documents, in declaration order.</param>
    /// <param name="scope">The composition root's own lowering scope, where the tests were collected.</param>
    /// <param name="stem">The source's own stem, which names each generated document.</param>
    /// <returns>The generated tests, in the order they were written.</returns>
    internal static IReadOnlyList<WorldTestWorld> LowerCompositionTests(IReadOnlyList<TestWorldSubject> worlds, DocumentScope scope, string stem) => LowerTests(
        composition: worlds,
        document: null,
        scope: scope,
        stem: stem
    );

    private static IReadOnlyList<WorldTestWorld> LowerTests(JsonObject? document, IReadOnlyList<TestWorldSubject>? composition, DocumentScope scope, string stem) {
        if (scope.Annotations.GetValueOrDefault(key: "IncludeTests") is false) { return []; }
        var tests = GetOrCreateTests(scope: scope);

        if (tests.Count == 0) {
            return [];
        }

        if (!GeneratedName.TryValidateAuthoredFile(
            name: stem,
            reason: out var stemReason
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.GeneratedNameReserved,
                message: $"this source's file name {stemReason}, so it cannot name the worlds its tests generate",
                span: tests[0].Declaration.Span
            );

            return [];
        }

        var worlds = new List<WorldTestWorld>(capacity: tests.Count);
        var generated = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var test in tests) {
            var name = GeneratedName.JoinFile(parts: [stem, .. (test.Qualifiers ?? []), Slug(text: test.Declaration.Name)]);

            if (generated.TryGetValue(
                key: name,
                value: out var taken
            )) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestShapeInadmissible,
                    message: $"test '{test.Name}' and test '{taken}' both generate the world '{name}' — two tests of one document cannot share a generated world, so give one of them a name that reduces differently",
                    span: test.Declaration.Span
                );

                continue;
            }

            generated[name] = test.Name;

            if (LowerTest(
                composition: composition,
                document: document,
                name: name,
                test: test
            ) is { } world) {
                worlds.Add(item: world);
            }
        }

        return worlds;
    }
    private static WorldTestWorld? LowerTest(CollectedTest test, JsonObject? document, IReadOnlyList<TestWorldSubject>? composition, string name) {
        if (test.Declaration.Expect is not { } expectations) {
            return null;
        }

        var scope = test.Scope;
        List<TestTarget> targets;

        if (test.Declaration.Subject is { } subject) {
            if (!TryLowerSubject(
                name: name,
                scope: ref scope,
                subject: subject,
                test: test,
                world: out var expanded
            )) {
                return null;
            }
            targets = [
                new TestTarget(
                Document: name,
                Json: expanded!,
                Scope: scope,
                World: null
            ),
            ];
        } else if (composition is { Count: > 0 } declared) {
            // One generated document per world of the composition, the entry world carrying the schedule that arms
            // the rest, as a boot of the source starts in it. A copy, so the composition's own outputs stay the
            // documents the source emits.
            if (declared.FirstOrDefault(predicate: static world => world.Entry) is not { } entry) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestShapeInadmissible,
                    message: $"test '{test.Name}' runs the composed worlds, and the composition declares no entry world to boot them from — write `entry world <name> = ...`",
                    span: test.Declaration.Span
                );

                return null;
            }

            targets = [
                .. declared.OrderBy(keySelector: world => ((world == entry) ? 0 : 1)).Select(selector: (world, index) => new TestTarget(
                    Document: ((index == 0)
                    ? name
                    : GeneratedName.AppendFile(
                        name: name,
                        part: world.Name
                    )),
                    Json: ((JsonObject)world.Json.DeepClone()),
                    Scope: world.Scope,
                    World: world.Name
                )),
            ];
        } else if (document is { } enclosing) {
            // The whole enclosing document, unchanged: a generated test world differs from the world it tests only
            // by what the test asked for. The boot decides the presentation — `puck test` boots headless — so
            // nothing here rewrites the host section.
            targets = [
                new TestTarget(
                Document: name,
                InheritedState: (scope.Annotations.GetValueOrDefault(key: InheritedStateAnnotation) as JsonObject),
                Json: ((JsonObject)enclosing.DeepClone()),
                Scope: scope,
                World: null
            ),
            ];
        } else {
            return null;
        }

        ApplyGiven(
            given: test.Declaration.Given,
            scope: scope,
            targets: targets,
            test: test
        );

        var exportTick = ApplyWhen(
            scope: scope,
            targets: targets,
            test: test,
            when: test.Declaration.When
        );

        var foreign = ApplyExpectations(
            expectations: expectations,
            exportTick: exportTick,
            scope: scope,
            targets: targets,
            test: test
        );
        ArmSiblings(targets: targets);
        RerootSiblingReferences(targets: targets);

        foreach (var target in targets) {
            WorldExpressionJson.Lower(
                diagnostics: scope.Diagnostics,
                node: target.Json
            );
            WorldChannelNodes.Lower(
                document: target.Json,
                type: typeof(WorldDefinition)
            );
        }

        return new WorldTestWorld(
            Json: ((JsonObject)DocumentLowering.Canonicalize(node: targets[0].Json)!),
            Name: name,
            Subject: ((test.Declaration.Subject is { } written)
                ? PuckPrinter.PrintExpression(expression: written)
                : null),
            Test: test.Name
        ) {
            Foreign = foreign,
            Siblings = [
                .. targets.Skip(count: 1).Select(selector: static target => new WorldTestSibling(
                    Json: ((JsonObject)DocumentLowering.Canonicalize(node: target.Json)!),
                    Name: target.Document
                )),
            ],
        };
    }
    // The boot document arms every other world of the composition, naming each generated sibling document beside it.
    private static void ArmSiblings(IReadOnlyList<TestTarget> targets) {
        if ((targets.Count < 2) || (targets[0].Json["schedule"] is not JsonObject schedule)) {
            return;
        }

        var instances = new JsonArray();

        foreach (var sibling in targets.Skip(count: 1)) {
            instances.AppendNode(item: new JsonObject {
                ["document"] = sibling.Document,
                ["name"] = sibling.World,
            });
        }
        schedule["instances"] = instances;
    }
    // A composition's own `references` rows name each sibling by its world name. A generated set
    // carries the test's own names instead, so the documents travel together in whatever directory a run writes them
    // into.
    private static void RerootSiblingReferences(IReadOnlyList<TestTarget> targets) {
        if (targets.Count < 2) {
            return;
        }

        var renamed = targets
            .Where(predicate: static target => (target.World is not null))
            .ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static target => target.Document,
            keySelector: static target => target.World!
        );

        foreach (var target in targets) {
            foreach (var reference in ((target.Json["references"] as JsonArray) ?? []).OfType<JsonObject>()) {
                if (
                    (reference["document"]?.GetValue<string>() is { } authored) &&
                    renamed.TryGetValue(
                    key: authored,
                    value: out var generated
                )
                ) {
                    reference["document"] = generated;
                }
            }
        }
    }
    // A test whose subject is a module stands the module up on its own: the named module expanded with the test's
    // arguments and nothing else, so what the verdict answers about is the module rather than whatever document
    // the test was written beside. The expansion's own scope carries the module's constants, its bound arguments
    // and the rows it declares, which is what the test's given and expect lines are written against.
    private static bool TryLowerSubject(CollectedTest test, CallExpressionNode subject, string name, ref DocumentScope scope, out JsonObject? world) {
        world = null;

        var host = scope;
        var moduleName = subject.Name;

        if (
            host.TryLowerBinding(subject.Name, out var bound) &&
            (bound is JsonValue leaf) &&
            leaf.TryGetValue<string>(value: out var boundName)
        ) {
            moduleName = boundName;
        }

        if (
            host.Templates.TryGetValue(
            key: moduleName,
            value: out var template
        ) &&
            !template.IsModule
        ) {
            host.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.TestShapeInadmissible,
                message: $"test '{test.Name}' is written with '{moduleName}', which is a template rather than a module — a template is a block of statements stamped where it is written, and only a module stands up as a world of its own",
                span: subject.Span
            );

            return false;
        }

        var root = new JsonObject();
        var annotations = CreateModuleAnnotations(expanded: root, scope: host);
        var child = host.WithConstants(new(host.Constants, StringComparer.Ordinal), annotations).WithLocals(lambdaLocals: host.Locals);

        using (host.SourceMap?.PushOrigin(moduleInstance: name))
        using (host.SourceMap?.PushIsolatedEntries()) {
            ExpandModuleUse(
                call: subject,
                invocation: out var invocation,
                scope: child,
                statement: new ExpressionStatementNode(subject, subject.Offset, subject.Length, subject.Line, subject.Column) { IsUse = true },
                target: root
            );

            if (invocation is not null) {
                scope = invocation;
            }
        }

        if (host.Diagnostics.HasErrors) {
            return false;
        }

        root["documentId"] = name;
        root["schema"] = WorldDefinition.SchemaVersion;
        world = root;

        return true;
    }
    // Which generated document a line lands in. A line written outside every world block belongs to the one world
    // the test is about; a source emitting several has no such world, so the line is refused rather than guessed at.
    private static TestTarget? ResolveTarget(IReadOnlyList<TestTarget> targets, string? world, string keyword, CollectedTest test, SourceSpan span, DocumentScope scope) {
        var composed = (targets[0].World is not null);

        if (world is null) {
            if (!composed) {
                return targets[0];
            }

            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.TestStepInadmissible,
                message: $"a '{keyword}' line of test '{test.Name}' names no world — this source emits its worlds by name and has no default one, so every line of a test at its root opens with the world it is about ({string.Join(
                    separator: ", ",
                    values: targets.Select(selector: static target => target.World)
                )})",
                span: span
            );

            return null;
        }

        if (!composed) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.TestStepInadmissible,
                message: $"a '{keyword}' line of test '{test.Name}' addresses the world '{world}' — a world block addresses one world of a composition, and this test is about a single world, so its lines are written unaddressed",
                span: span
            );

            return null;
        }

        foreach (var target in targets) {
            if (string.Equals(
                a: target.World,
                b: world,
                comparisonType: StringComparison.Ordinal
            )) {
                return target;
            }
        }
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            message: $"a '{keyword}' line of test '{test.Name}' addresses '{world}', which this source declares no world by — the worlds are {string.Join(
                separator: ", ",
                values: targets.Select(selector: static target => target.World)
            )}",
            span: span
        );

        return null;
    }
    // A `given` line writes the cell's boot value on the generated world's own state row, where the enclosing
    // world authored it. A row the document does not declare, or a value of another kind than the row holds, is
    // refused by the same texts an authored cell is.
    private static void ApplyGiven(TestGivenBlockNode? given, IReadOnlyList<TestTarget> targets, CollectedTest test, DocumentScope scope) {
        if (given is not { Cells.Count: > 0 }) {
            return;
        }

        foreach (var (addressed, cell) in given.Lines) {
            if (ResolveTarget(
                keyword: "given",
                scope: scope,
                span: cell.Span,
                targets: targets,
                test: test,
                world: addressed
            ) is not { } target) {
                continue;
            }

            var world = target.Json;
            var rows = ((world["state"] as JsonObject)?["world"] as JsonArray);
            var cellScope = target.Scope;
            var row = rows?
                .OfType<JsonObject>()
                .FirstOrDefault(predicate: candidate => (candidate["name"]?.GetValue<string>() == cell.Target.Name));
            var inheritedRow = ((row is null)
                ? (target.InheritedState?["world"] as JsonArray)?
                    .OfType<JsonObject>()
                    .FirstOrDefault(predicate: candidate => (candidate["name"]?.GetValue<string>() == cell.Target.Name))
                : null);

            if ((row is null) && (inheritedRow is null)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"a given line writes '{cell.Target.Name}', which this world's state section does not declare",
                    span: cell.Span
                );

                continue;
            }

            var kind = ((row ?? inheritedRow)!["kind"]?.GetValue<string>() ?? nameof(CellKind.Int));
            var key = (cell.Target.Key ?? StateRow.SlotKey.Value);
            var value = (cell.Value switch {
                RhsSecondsNode seconds => JsonValue.Create(value: seconds.Seconds),
                RhsTextNode or RhsOperandNode => GivenLiteral(
                    cell: cell,
                    kind: kind,
                    scope: cellScope
                ),
                _ => null,
            });

            if (value is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"a given line writes '{cell.Target.Name}' from something other than a literal — a test's given block states boot values, which the document carries as numbers and strings",
                    span: cell.Span
                );

                continue;
            }

            if (row is null) {
                var slotShaped = (
                    (inheritedRow!["cells"] is not JsonArray) &&
                    ((inheritedRow["domain"] as JsonObject)?["$type"]?.GetValue<string>() is null or "slot")
                );

                if (slotShaped && (key != StateRow.SlotKey.Value)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"a given line writes '{cell.Target.Name}['{key}']', but that row is slot-shaped — its one cell is addressed by the row's own name",
                        span: cell.Span
                    );

                    continue;
                }

                if (world["state"] is not JsonObject state) {
                    state = [];
                    world["state"] = state;
                }
                if (state["world"] is not JsonArray stateRows) {
                    stateRows = [];
                    state["world"] = stateRows;
                }

                row = (slotShaped
                    ? new JsonObject {
                        ["kind"] = kind,
                        ["name"] = cell.Target.Name,
                        ["value"] = value,
                    }
                    : new JsonObject {
                        ["cells"] = new JsonArray(),
                        ["kind"] = kind,
                        ["name"] = cell.Target.Name,
                    });
                stateRows.AppendNode(item: row);

                if (slotShaped) {
                    continue;
                }
            }

            // `value` IS the one-cell spelling of `cells` on a slot-shaped row, and a document authoring both is
            // refused, so a given line addressing the reserved slot key writes through whichever spelling the row
            // already carries.
            if (
                (key == StateRow.SlotKey.Value) &&
                (row["cells"] is not JsonArray)
            ) {
                row["value"] = value;

                continue;
            }

            if (row["value"] is not null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"a given line writes '{cell.Target.Name}['{key}']', but that row is slot-shaped — its one cell is addressed by the row's own name",
                    span: cell.Span
                );

                continue;
            }

            if (row["cells"] is not JsonArray cells) {
                cells = [];
                row["cells"] = cells;
            }

            var existing = cells
                .OfType<JsonObject>()
                .FirstOrDefault(predicate: candidate => (candidate["key"]?.GetValue<string>() == key));

            if (existing is not null) {
                existing["value"] = value;

                continue;
            }

            cells.AppendNode(item: new JsonObject { ["key"] = key, ["value"] = value });
        }
    }
    // A given value is a literal of the row's own kind, lowered by the door an authored cell's value takes, so it
    // is spelled in the document exactly as that cell would be and refused by the same texts.
    private static JsonNode? GivenLiteral(TestGivenNode cell, string kind, DocumentScope scope) {
        var expression = cell.Value switch {
            RhsTextNode text => new LiteralExpressionNode(text.Text),
            RhsOperandNode operand => CompileTimeOperand(operand: operand.Expression, scope: scope),
            _ => null,
        };

        if ((expression is IdentifierExpressionNode name) && !scope.TryLowerBinding(name.Name, out _)) { return null; }
        return ((expression is null) ? null : LowerStateScalarValue(
            context: $"a given line's value for '{cell.Target.Name}'",
            expr: expression, kind: kind, scope: scope));
    }
    // The tick grid: the cursor opens at the first tick a command can be submitted at and `ticks n` carries it
    // forward, so a seat step lands on the tick its preceding steps reached. The export tick is the last tick the
    // grid reaches, which is also the tick every expectation is decided at.
    private static ulong ApplyWhen(TestWhenBlockNode? when, IReadOnlyList<TestTarget> targets, CollectedTest test, DocumentScope scope) {
        var rows = new JsonArray();
        var cursor = 1UL;
        var last = 0UL;

        foreach (var (addressed, step) in (((IEnumerable<(string? World, TestStepNode Step)>?)when?.Lines) ?? [])) {
            if (step is TestTicksStepNode ticks) {
                cursor += ((ulong)ticks.Ticks);

                continue;
            }

            if (step is not TestSeatStepNode seat) {
                continue;
            }

            var verb = WorldScheduleCommands.LeadingVerb(command: seat.Command);

            if (!WorldScheduleCommands.IsAdmitted(verb: verb)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"'{verb}' is not a scheduled step verb — a test's when block carries the state, transform, body and session verbs a seat may submit at an exact tick and the state reads its own disclosure answers ({string.Join(
                        separator: ", ",
                        values: WorldScheduleCommands.Admitted
                    )}), never a verb that touches the process, the clock, the file system or the grant table",
                    span: seat.Span
                );

                continue;
            }

            if (ResolveTarget(
                keyword: "when",
                scope: scope,
                span: seat.Span,
                targets: targets,
                test: test,
                world: addressed
            ) is not { } target) {
                continue;
            }

            var row = new JsonObject {
                ["command"] = seat.Command,
                ["principal"] = $"seat{seat.Seat.ToString(provider: CultureInfo.InvariantCulture)}",
                ["tick"] = cursor,
            };

            if (seat.Refused) {
                row["expect"] = nameof(WorldScheduleExpectation.Refused);
                if (seat.Refusal is { } refusal) {
                    row["refusal"] = refusal;
                }
            }

            // A row reaches a world beside the booted one by riding the trailing `instance:` token the verb's own
            // grammar reads, so only the verbs carrying one can be addressed at a sibling.
            if (!ReferenceEquals(
                objA: target,
                objB: targets[0]
            )) {
                if (!WorldScheduleCommands.IsAddressable(verb: verb)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"'{verb}' addresses the world '{target.World}', and its grammar carries no world token — every other verb reaches the world the run boots with, '{targets[0].World}', whatever a step asks for. The verbs a step may address another world with are {string.Join(
                            separator: ", ",
                            values: WorldScheduleCommands.Addressable
                        )}; drive another world's state through what crosses into it",
                        span: seat.Span
                    );

                    continue;
                }
                row["world"] = target.World;
            }
            rows.AppendNode(item: row);
            last = cursor;
        }

        // The grid always runs at least `MinimumSettleTicks` past the last step, whatever the author wrote after
        // it.
        var settle = ((last == 0UL)
            ? Math.Max(
            val1: 1UL,
            val2: cursor
        )
            : Math.Max(
            val1: MinimumSettleTicks,
            val2: (cursor - last)
        ));
        // A world armed at boot counts from its own zero, so it stands one tick behind the booted world at the host
        // step the exports are taken on. A composed run's grid therefore has to reach at least tick 2, or the far
        // world's own verdict tick would be a tick nothing publishes.
        var exportTick = (last + settle);

        if ((targets.Count > 1) && (exportTick < 2UL)) {
            settle += (2UL - exportTick);
            exportTick = (last + settle);
        }
        targets[0].Json["schedule"] = new JsonObject {
            ["rows"] = rows,
            ["settleTicks"] = settle,
        };

        return exportTick;
    }
    // The tick a world's own verdict rule fires on. Every world is exported at the one host step the booted world's
    // export tick falls on; an instance armed at boot advances beside it but counts from its own zero, so its
    // export carries the tick before.
    private static ulong VerdictTick(IReadOnlyList<TestTarget> targets, TestTarget target, ulong exportTick) => (ReferenceEquals(
        objA: target,
        objB: targets[0]
    )
        ? exportTick
        : (exportTick - 1UL)
    );
    // One verdict row and one rule per expectation. The rule's gate is the export tick and nothing else, so it
    // fires exactly once: an expectation that is false before it becomes true would otherwise settle the verdict
    // at fail on the first tick, since a failed verdict stays failed.
    //
    // A line addressed by name in a test about one world is a claim about the sibling document of that name, which a
    // crossing starts: it cannot be decided at a tick, since the document starts whenever the traveller arrives. Its
    // rule is gated on the claim itself and writes the verdict whenever the claim holds, and the lines are returned
    // for `puck test` to lower into the document once it has composed it.
    private static List<WorldTestForeign> ApplyExpectations(TestExpectBlockNode expectations, IReadOnlyList<TestTarget> targets, DocumentScope scope, CollectedTest test, ulong exportTick) {
        var foreign = new List<WorldTestForeign>();
        var line = 0;

        foreach (var (addressed, expectation) in expectations.Lines) {
            line++;

            if ((addressed is not null) && (targets[0].World is null)) {
                foreign.Add(item: LowerForeign(
                    document: addressed,
                    expectation: expectation,
                    line: line,
                    scope: scope
                ));

                continue;
            }

            if (ResolveTarget(
                keyword: "expect",
                scope: scope,
                span: expectation.Span,
                targets: targets,
                test: test,
                world: addressed
            ) is not { } target) {
                continue;
            }

            var world = target.Json;

            if (world["state"] is not JsonObject state) {
                state = [];
                world["state"] = state;
            }

            if (state["world"] is not JsonArray rows) {
                rows = [];
                state["world"] = rows;
            }

            if (world["rules"] is not JsonArray rules) {
                rules = [];
                world["rules"] = rules;
            }

            var condition = LowerPredicate(
                node: expectation.Predicate,
                scope: target.Scope
            );
            var gate = PuckPrinter.PrintGate(predicate: expectation.Predicate);
            var row = VerdictRow(line: line);

            // No author writes a `$` inside a rule's name, but a scope named `expect` joins its own name to a member
            // rule named `1` the same way, so the one collision a generated rule can meet is another generated one.
            if (rules.OfType<JsonObject>().Any(predicate: candidate => (candidate["name"]?.GetValue<string>() == row))) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestShapeInadmissible,
                    message: $"test '{test.Name}' would generate the verdict rule '{row}', which a rule scope of this world already generated",
                    span: expectation.Span
                );

                continue;
            }

            var reads = new List<(string State, string? Key)>();

            CollectGateReads(
                into: reads,
                node: condition
            );

            var effects = new JsonArray();
            var pass = new JsonArray();
            var fail = new JsonArray();

            fail.AppendNode(item: SetVerdictCell(
                key: VerdictStatusKey,
                row: row,
                value: WorldVerdict.Fail
            ));
            pass.AppendNode(item: SetVerdictCell(
                key: VerdictStatusKey,
                row: row,
                value: WorldVerdict.Pass
            ));
            effects.AppendNode(item: new JsonObject {
                ["$type"] = "if",
                ["condition"] = condition,
                ["else"] = fail,
                ["then"] = pass,
            });
            AddVerdictRows(
                effects: effects,
                gate: gate,
                inheritedState: target.InheritedState,
                reads: reads,
                row: row,
                rows: rows,
                state: state
            );
            rules.AppendNode(item: new JsonObject {
                ["effects"] = effects,
                ["gate"] = new JsonObject {
                    ["$type"] = "compareState",
                    ["comparison"] = "Equal",
                    ["state"] = RuleFacts.Tick,
                    ["value"] = VerdictTick(
                    exportTick: exportTick,
                    target: target,
                    targets: targets
                ),
                },
                ["mode"] = "Level",
                ["name"] = row,
            });
        }

        return foreign;
    }
    // A line about a sibling document, lowered as far as the test's own document can: the rule that records the claim
    // holding, with the rows its gate reads left for the document to answer once it is composed.
    private static WorldTestForeign LowerForeign(string document, TestExpectationNode expectation, int line, DocumentScope scope) {
        var condition = LowerPredicate(
            node: expectation.Predicate,
            scope: scope
        );
        var reads = new List<(string State, string? Key)>();

        CollectGateReads(
            into: reads,
            node: condition
        );

        var row = VerdictRow(line: line);
        var rule = new JsonObject {
            ["effects"] = new JsonArray(SetVerdictCell(
                key: VerdictStatusKey,
                row: row,
                value: WorldVerdict.Pass
            )),
            ["gate"] = condition,
            ["mode"] = "Level",
            ["name"] = row,
        };
        var holder = new JsonObject { ["rules"] = new JsonArray(rule) };

        WorldExpressionJson.Lower(
            diagnostics: scope.Diagnostics,
            node: holder
        );
        WorldChannelNodes.Lower(
            document: holder,
            type: typeof(WorldDefinition)
        );
        holder["rules"]!.AsArray().Clear();

        return new WorldTestForeign(
            Document: document,
            Gate: PuckPrinter.PrintGate(predicate: expectation.Predicate),
            Line: line,
            Reads: reads,
            Rule: rule
        );
    }
    // The verdict row for one expectation, and one witness row for each kind other than Int its gate read, appended to
    // `rows`; the effects that copy what the gate read into them are appended to `effects`, after the one that decides
    // the verdict.
    internal static void AddVerdictRows(JsonObject state, JsonArray rows, JsonArray effects, string row, string gate, IReadOnlyList<(string State, string? Key)> reads, JsonObject? inheritedState = null) {
        var cells = new JsonArray();

        cells.AppendNode(item: new JsonObject { ["key"] = VerdictStatusKey, ["value"] = WorldVerdict.NotEvaluated });

        var folded = new HashSet<(CellKind Kind, string Key)> { (CellKind.Int, VerdictStatusKey) };
        var witnesses = new SortedDictionary<CellKind, JsonArray>();

        foreach (var (readState, readKey) in reads) {
            // A row holds one kind, so what the gate read of an Int row is a cell of the verdict row and what it
            // read of a Fixed, Bool or Text row is a cell of that kind's witness.
            if (
                ((KindOfRow(
                    name: readState,
                    state: state
                ) ?? ((inheritedState is null)
                    ? null
                    : KindOfRow(
                        name: readState,
                        state: inheritedState
                    ))) is not { } kind) ||
                ((kind != CellKind.Int) && (UnseenValue(kind: kind) is null))
            ) {
                continue;
            }

            if ((SawKey(
                key: readKey,
                state: readState
            ) is not { } key) || !folded.Add(item: (kind, key))) {
                continue;
            }

            var into = row;

            if (kind == CellKind.Int) {
                cells.AppendNode(item: new JsonObject { ["key"] = key, ["value"] = 0 });
            } else {
                into = WitnessRow(
                    kind: kind,
                    verdict: row
                );

                if (!witnesses.TryGetValue(
                    key: kind,
                    value: out var witnessCells
                )) {
                    witnessCells = [];
                    witnesses[kind] = witnessCells;
                }
                witnessCells.AppendNode(item: new JsonObject { ["key"] = key, ["value"] = UnseenValue(kind: kind) });
            }

            var fold = new JsonObject {
                ["$type"] = "setState",
                ["fromState"] = readState,
                ["key"] = key,
                ["state"] = into,
            };

            if (readKey is not null) {
                fold["fromKey"] = readKey;
            }
            effects.AppendNode(item: fold);
        }
        // No `generated` flag: `StateRow.Generated` is engine-minted and no document member carries it, so
        // what marks these rows as machinery is the verdict trait itself.
        rows.AppendNode(item: new JsonObject {
            ["cells"] = cells,
            ["kind"] = "Int",
            ["name"] = row,
            ["verdict"] = new JsonObject {
                ["gate"] = Trimmed(gate: gate),
                ["status"] = VerdictStatusKey,
            },
        });
        foreach (var (kind, witnessCells) in witnesses) {
            rows.AppendNode(item: new JsonObject {
                ["cells"] = witnessCells,
                ["kind"] = kind.ToString(),
                ["name"] = WitnessRow(
                    kind: kind,
                    verdict: row
                ),
                ["witness"] = row,
            });
        }
    }
    // The verdict trait's gate ceiling, applied here so a long expectation reports as a verdict rather than as a
    // boot refusal.
    private static string Trimmed(string gate) => ((gate.Length <= WorldVerdict.MaxGateLength)
        ? gate
        : gate[..WorldVerdict.MaxGateLength]
    );
}
