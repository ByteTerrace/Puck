using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

/// <summary>One generated test world: the enclosing world's own document plus what a <c>test</c> block asked
/// for.</summary>
/// <param name="Name">The world's own name, which is the enclosing source's stem and the test's slug — the name
/// <c>puck test</c> reports a verdict under and the stem of the document a <c>--keep</c> run writes.</param>
/// <param name="Test">The test's authored name.</param>
/// <param name="Json">The canonical generated document.</param>
public sealed record WorldTestWorld(string Name, string Test, JsonObject Json);

// A `test` block reaches no member of the document it is written in: it is collected while that document lowers
// and each collected test is lowered afterwards against the finished document, so a world compiled without
// `puck test` is byte-identical to the same source with its tests deleted.
public static partial class WorldDocumentEmitter {
    private const string TestsAnnotation = "WorldTests";
    // The status cell of a generated verdict row. The other cells are the values the expectation read, plus the
    // engine's own `$firedTick` stamp.
    private const string VerdictStatusKey = "status";
    // The floor on the margin between a test's last scheduled step and its export tick. A step is SUBMITTED at its
    // tick; the host's command pump drains it, the simulation lane applies it, and the run records the answer the
    // reconciliation reads — each on a later tick. A margin below this floor reaches the export with a row still
    // awaiting its answer, which the reconciliation refuses.
    private const ulong MinimumSettleTicks = 6UL;

    private static List<TestDeclarationNode> GetOrCreateTests(DocumentScope scope) {
        if (
            !scope.Annotations.TryGetValue(
            key: TestsAnnotation,
            value: out var held
        ) ||
            (held is not List<TestDeclarationNode> tests)
        ) {
            tests = [];
            scope.Annotations[TestsAnnotation] = tests;
        }

        return tests;
    }
    // A name a file, a row and a rule can all carry: the authored text reduced to lowercase words joined by
    // dashes. Two tests reducing to the same slug are refused, since their generated worlds would collide.
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
    // The cell key a folded value is recorded under: the operand's own row and key, with the characters a cell name
    // may not carry replaced by dashes.
    private static string SawKey(string state, string? key) {
        var spelling = ((key is null) || (key == StateRow.SlotKey.Value)
            ? state
            : $"{state}-{key}"
        );
        var builder = new StringBuilder();

        foreach (var character in spelling) {
            _ = builder.Append(value: (char.IsAsciiLetterOrDigit(c: character)
                ? character
                : '-'
            ));
        }

        return builder.ToString().Trim(trimChar: '-');
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
        _ => null,
    };
    // The row that holds what a verdict's gate saw of rows of one kind other than Int.
    private static string WitnessRow(string verdict, CellKind kind) => $"{verdict}-{kind.ToString().ToLowerInvariant()}";
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
    internal static IReadOnlyList<WorldTestWorld> LowerTests(JsonObject document, DocumentScope scope, string stem) {
        var tests = GetOrCreateTests(scope: scope);

        if (tests.Count == 0) {
            return [];
        }

        var worlds = new List<WorldTestWorld>(capacity: tests.Count);
        var slugs = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var test in tests) {
            var slug = Slug(text: test.Name);

            if (slugs.TryGetValue(
                key: slug,
                value: out var taken
            )) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestShapeInadmissible,
                    message: $"test '{test.Name}' and test '{taken}' both generate the world '{stem}--{slug}' — two tests of one document cannot share a generated world, so give one of them a name that reduces differently",
                    span: test.Span
                );

                continue;
            }

            slugs[slug] = test.Name;

            if (LowerTest(
                document: document,
                name: $"{stem}--{slug}",
                scope: scope,
                slug: slug,
                test: test
            ) is { } world) {
                worlds.Add(item: world);
            }
        }

        return worlds;
    }
    private static WorldTestWorld? LowerTest(TestDeclarationNode test, JsonObject document, DocumentScope scope, string name, string slug) {
        if (test.Expect is not { Expectations.Count: > 0 } expectations) {
            return null;
        }

        // The whole enclosing document, unchanged: a generated test world differs from the world it tests only by
        // what the test asked for. The boot decides the presentation — `puck test` boots headless — so nothing
        // here rewrites the host section.
        var world = ((JsonObject)document.DeepClone());

        ApplyGiven(
            given: test.Given,
            scope: scope,
            world: world
        );

        var exportTick = ApplyWhen(
            scope: scope,
            when: test.When,
            world: world
        );

        ApplyExpectations(
            exportTick: exportTick,
            expectations: expectations,
            scope: scope,
            slug: slug,
            test: test,
            world: world
        );
        WorldExpressionJson.Lower(
            diagnostics: scope.Diagnostics,
            node: world
        );

        return new WorldTestWorld(
            Json: ((JsonObject)Canonicalize(node: world)!),
            Name: name,
            Test: test.Name
        );
    }
    // A `given` line writes the cell's boot value on the generated world's own state row, where the enclosing
    // world authored it. A row the document does not declare, or a value of another kind than the row holds, is
    // refused by the same texts an authored cell is.
    private static void ApplyGiven(TestGivenBlockNode? given, JsonObject world, DocumentScope scope) {
        if (given is not { Cells.Count: > 0 }) {
            return;
        }

        var rows = ((world["state"] as JsonObject)?["world"] as JsonArray);

        foreach (var cell in given.Cells) {
            var row = rows?
                .OfType<JsonObject>()
                .FirstOrDefault(predicate: candidate => (candidate["name"]?.GetValue<string>() == cell.Target.Name));

            if (row is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestStepInadmissible,
                    message: $"a given line writes '{cell.Target.Name}', which this world's state section does not declare",
                    span: cell.Span
                );

                continue;
            }

            var key = (cell.Target.Key ?? StateRow.SlotKey.Value);
            var value = (cell.Value switch {
                RhsSecondsNode seconds => JsonValue.Create(value: seconds.Seconds),
                RhsTextNode or RhsOperandNode => GivenLiteral(
                    cell: cell,
                    kind: (row["kind"]?.GetValue<string>() ?? nameof(CellKind.Int)),
                    scope: scope
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
        object? literal = null;
        string? raw = null;

        if (cell.Value is RhsTextNode quoted) {
            literal = quoted.Text;
        } else if (cell.Value is RhsOperandNode operand) {
            raw = ResolveOperandConstants(
                scope: scope,
                text: operand.Text
            ).Trim();

            if (bool.TryParse(
                result: out var flag,
                value: raw
            )) {
                literal = flag;
            } else if (long.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var whole,
                s: raw,
                style: NumberStyles.AllowLeadingSign
            )) {
                literal = whole;
            } else if (double.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var real,
                s: raw,
                style: (NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign)
            )) {
                literal = real;
            }
        }

        return ((literal is null)
            ? null
            : LowerStateScalarValue(
                context: $"a given line's value for '{cell.Target.Name}'",
                expr: new LiteralExpressionNode(
                    Column: cell.Value.Column,
                    Length: cell.Value.Length,
                    Line: cell.Value.Line,
                    Offset: cell.Value.Offset,
                    RawText: raw,
                    Value: literal
                ),
                kind: kind,
                scope: scope
            )
        );
    }
    // The tick grid: the cursor opens at the first tick a command can be submitted at and `ticks n` carries it
    // forward, so a seat step lands on the tick its preceding steps reached. The export tick is the last tick the
    // grid reaches, which is also the tick every expectation is decided at.
    private static ulong ApplyWhen(TestWhenBlockNode? when, JsonObject world, DocumentScope scope) {
        var rows = new JsonArray();
        var cursor = 1UL;
        var last = 0UL;

        foreach (var step in (when?.Steps ?? [])) {
            switch (step) {
                case TestTicksStepNode ticks:
                    cursor += ((ulong)ticks.Ticks);

                    break;
                case TestSeatStepNode seat when !WorldScheduleCommands.IsAdmitted(verb: WorldScheduleCommands.LeadingVerb(command: seat.Command)):
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.TestStepInadmissible,
                        message: $"'{WorldScheduleCommands.LeadingVerb(command: seat.Command)}' is not a scheduled step verb — a test's when block carries the state, transform, body and session verbs a seat may submit at an exact tick ({string.Join(
                            separator: ", ",
                            values: WorldScheduleCommands.Admitted
                        )}), never a verb that touches the process, the clock, the file system or the grant table",
                        span: seat.Span
                    );

                    break;
                case TestSeatStepNode seat:
                    rows.AppendNode(item: new JsonObject {
                        ["command"] = seat.Command,
                        ["principal"] = $"seat{seat.Seat.ToString(provider: CultureInfo.InvariantCulture)}",
                        ["tick"] = cursor,
                    });
                    last = cursor;

                    break;
                default:
                    break;
            }
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

        world["schedule"] = new JsonObject {
            ["directory"] = "out",
            ["rows"] = rows,
            ["settleTicks"] = settle,
        };

        return (last + settle);
    }
    // One verdict row and one rule per expectation. The rule's gate is the export tick and nothing else, so it
    // fires exactly once: an expectation that is false before it becomes true would otherwise settle the verdict
    // at fail on the first tick, since a failed verdict stays failed.
    private static void ApplyExpectations(TestExpectBlockNode expectations, JsonObject world, DocumentScope scope, TestDeclarationNode test, string slug, ulong exportTick) {
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

        var line = 0;

        foreach (var expectation in expectations.Expectations) {
            line++;

            var condition = LowerPredicate(
                node: expectation.Predicate,
                scope: scope
            );
            var gate = PuckPrinter.PrintGate(predicate: expectation.Predicate);
            var row = $"{slug}-{line.ToString(provider: CultureInfo.InvariantCulture)}";

            if (rows.OfType<JsonObject>().Any(predicate: candidate => (candidate["name"]?.GetValue<string>() == row))) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.TestShapeInadmissible,
                    message: $"test '{test.Name}' would generate the verdict row '{row}', which this world already declares",
                    span: expectation.Span
                );

                continue;
            }

            var reads = new List<(string State, string? Key)>();

            CollectGateReads(
                into: reads,
                node: condition
            );

            var cells = new JsonArray();
            var effects = new JsonArray();
            var pass = new JsonArray();
            var fail = new JsonArray();

            cells.AppendNode(item: new JsonObject { ["key"] = VerdictStatusKey, ["value"] = WorldVerdict.NotEvaluated });
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
            var folded = new HashSet<(CellKind Kind, string Key)> { (CellKind.Int, VerdictStatusKey) };
            var witnesses = new SortedDictionary<CellKind, JsonArray>();

            foreach (var (readState, readKey) in reads) {
                // A row holds one kind, so what the gate read of an Int row is a cell of the verdict row and what it
                // read of a Fixed or a Bool row is a cell of that kind's witness.
                if (
                    (KindOfRow(
                        name: readState,
                        state: state
                    ) is not { } kind) ||
                    ((kind != CellKind.Int) && (UnseenValue(kind: kind) is null))
                ) {
                    continue;
                }

                var key = SawKey(
                    key: readKey,
                    state: readState
                );

                if ((key.Length == 0) || !folded.Add(item: (kind, key))) {
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
                var witness = WitnessRow(
                    kind: kind,
                    verdict: row
                );

                if (rows.OfType<JsonObject>().Any(predicate: candidate => (candidate["name"]?.GetValue<string>() == witness))) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.TestShapeInadmissible,
                        message: $"test '{test.Name}' would generate the witness row '{witness}', which this world already declares",
                        span: expectation.Span
                    );

                    continue;
                }
                rows.AppendNode(item: new JsonObject {
                    ["cells"] = witnessCells,
                    ["kind"] = kind.ToString(),
                    ["name"] = witness,
                    ["witness"] = row,
                });
            }
            rules.AppendNode(item: new JsonObject {
                ["effects"] = effects,
                ["gate"] = new JsonObject {
                    ["$type"] = "compareState",
                    ["comparison"] = "Equal",
                    ["state"] = RuleFacts.Tick,
                    ["value"] = exportTick,
                },
                ["mode"] = "Level",
                ["name"] = $"test-{row}",
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
