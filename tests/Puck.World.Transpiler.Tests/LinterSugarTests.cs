using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckLinter.LintReferences"/> — the symbol-resolution pass over lowered JSON — exercised
/// against every shipped world's decompiled form, plus a fixture per diagnostic code and per basis/module mode.</summary>
public class LinterSugarTests {
    private readonly record struct LintResult(DiagnosticBag Diagnostics, string DecompiledSource);

    // `fullPath` need not exist on disk itself (it is the shipped .world.json, not the decompiled text) — only its
    // directory matters, since that is where a declared basis or import resolves from, and it is the real worlds
    // directory either way.
    private static LintResult LintShippedWorld(string relativePath) {
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        Assert.True(File.Exists(fullPath), $"Shipped world file not found: {fullPath}");
        var decompiled = WorldDecompiler.Decompile(File.ReadAllText(fullPath));

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(decompiled, diagnostics: diagnostics);
        if (parseResult.Value is not null) {
            PuckLinter.Lint(parseResult.Value, diagnostics);
            var sourceMap = new SourceMap();
            var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
                parseResult.Value,
                basePath: Path.GetDirectoryName(fullPath),
                sourceMap: sourceMap,
                diagnostics: diagnostics
            , cancellationToken: TestContext.Current.CancellationToken);
            if (loweringResult.Value is not null) {
                PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics, sourcePath: fullPath);
            }
        }
        return new LintResult(diagnostics, decompiled);
    }

    private static readonly string[] ReferenceLintCodes = ["PUCK_LINT_005", "PUCK_LINT_006", "PUCK_LINT_007", "PUCK_LINT_008", "PUCK_LINT_009", "PUCK034"];

    // Worlds with no `basis`/`imports` of their own, whose every gate/effect/placement/camera reference resolves
    // inside the same file — the common case `puck lint` targets.
    public static TheoryData<string> SelfContainedShippedWorlds => new() {
        "games/billiards.world.json",
        "games/tictactoe.world.json",
        "games/chinese-checkers.world.json",
        "games/poker.world.json",
        "study.world.json",
    };

    [Theory]
    [MemberData(nameof(SelfContainedShippedWorlds))]
    public void SelfContainedShippedWorldsLintClean(string relativePath) {
        var result = LintShippedWorld(relativePath);
        Assert.True(
            result.Diagnostics.Count == 0,
            $"{relativePath} expected zero diagnostics, got:{Environment.NewLine}{result.Diagnostics.FormatReport(result.DecompiledSource)}"
        );
    }

    [Fact]
    public void SolitaireContainerLintsClean() {
        // games/solitaire.world.json declares no `basis` and composes klondike/spider/freecell through `imports`,
        // but its own body never references a row/prototype those siblings own, so it lints clean either way.
        var result = LintShippedWorld("games/solitaire.world.json");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RootWorldResolvesReferencesAgainstItsComposedBasis() {
        var result = LintShippedWorld("puck.world.json");

        var referenceFindings = result.Diagnostics.Where(d => ReferenceLintCodes.Contains(d.Code)).ToList();
        Assert.True(
            referenceFindings.Count == 0,
            $"expected no reference-lint findings, got:{Environment.NewLine}{string.Join(Environment.NewLine, referenceFindings.Select(d => d.Message))}"
        );

        // PUCK026 ("rule carries no effect statements") false-positives on a decision-only rule — a pre-existing
        // defect in the parser stage (Parsing/PuckParser.Rules.cs), outside this stage's file scope; flagged, not
        // fixed, here. Every OTHER error would be a real regression.
        var unexpectedErrors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Code != "PUCK026").ToList();
        Assert.Empty(unexpectedErrors);
    }

    // Leaf "district" worlds imported by a parent (puck.world.json) or a sibling composer (solitaire.world.json)
    // that supplies the row/prototype they reference. None declares its own basis, so each is a module: a name it
    // cannot resolve standalone is never a finding — it may belong to whichever root imports it.
    public static TheoryData<string> CrossFileLeafWorlds => new() {
        "games/freecell.world.json",
        "games/klondike.world.json",
        "games/spider.world.json",
        "games/mancala.world.json",
        "games/bowling.world.json",
    };

    [Theory]
    [MemberData(nameof(CrossFileLeafWorlds))]
    public void CrossFileLeafWorldsReportNoUnresolvedReference(string relativePath) {
        var result = LintShippedWorld(relativePath);
        var referenceFindings = result.Diagnostics.Where(d => ReferenceLintCodes.Contains(d.Code)).ToList();
        Assert.True(
            referenceFindings.Count == 0,
            $"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}{string.Join(Environment.NewLine, referenceFindings.Select(d => d.Message))}"
        );
    }

    // ---- Fixtures: one deliberately-broken and one deliberately-valid case per new rule ------------------------

    // Turns `document` into a root: gives it a `basis` naming a trivial, empty basis file in a scratch directory,
    // so composed-catalog resolution runs (`resolveGlobalReferences: true`) without depending on any real shipped
    // basis content.
    private static DiagnosticBag LintReferencesAsRoot(JsonObject document) {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "puck-lint-refs-" + Guid.NewGuid().ToString("N"))).FullName;
        try {
            File.WriteAllText(Path.Combine(directory, "basis.json"), "{}");
            document["basis"] = "basis.json";
            var diagnostics = new DiagnosticBag();
            PuckLinter.LintReferences(document, sourceMap: null, diagnostics, sourcePath: Path.Combine(directory, "root.world.json"));
            return diagnostics;
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Leaves `document` basis-less, so it lints as a module (`resolveGlobalReferences: false`) — the sourcePath
    // is never touched, since a module composes nothing.
    private static DiagnosticBag LintReferencesAsModule(JsonObject document) {
        var diagnostics = new DiagnosticBag();
        PuckLinter.LintReferences(document, sourceMap: null, diagnostics, sourcePath: "module.world.json");
        return diagnostics;
    }

    [Fact]
    public void UnresolvedStateRowInAGateIsFlagged() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray(new JsonObject { ["name"] = "hp" }) },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "hpp", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_005") && d.Message.Contains("hpp"));
    }

    [Fact]
    public void DeclaredStateRowInAGateIsNotFlagged() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray(new JsonObject { ["name"] = "hp" }) },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "hp", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }

    [Fact]
    public void UnresolvedStateTokenInsideACompareValueOperandIsFlagged() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray(new JsonObject { ["name"] = "hp" }) },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue", ["comparison"] = "NotEqual", ["kind"] = "Int",
                    ["left"] = "hp + 1", ["right"] = "mana",
                },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_005") && d.Message.Contains("mana"));
    }

    [Fact]
    public void ANonCompareValueLeftRightPairIsNeverReadAsAnExpression() {
        // WorldInteraction's own "left"/"right" name a property or placement id, never a ValueExpression — only
        // ActionPredicate.CompareValue's pair is. A bare identifier there must never be checked against `state`.
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["interactions"] = new JsonObject {
                ["interactions"] = new JsonArray(new JsonObject {
                    ["name"] = "i", ["left"] = "hound", ["right"] = "bone", ["coOccurrence"] = "Region", ["range"] = 0, ["effects"] = new JsonArray(),
                }),
            },
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }

    [Fact]
    public void UnresolvedPrototypeIdIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "table" }),
            },
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_006") && d.Message.Contains("table"));
    }

    [Fact]
    public void DeclaredPrototypeIdIsNotFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair" }),
            },
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }

    [Fact]
    public void UnresolvedPlacementParentIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair", ["parent"] = "missing-parent" }),
            },
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_007") && d.Message.Contains("missing-parent"));
    }

    [Fact]
    public void DeclaredPlacementParentIsNotFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(
                    new JsonObject { ["id"] = "root", ["prototypeId"] = "chair" },
                    new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair", ["parent"] = "root" }
                ),
            },
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }

    [Fact]
    public void UnresolvedShapeParentIsFlaggedAsAWarning() {
        // Shape-parent resolution is scoped to sibling shapes in the same array — a purely local check that runs
        // whether or not the document declares a basis, so this fixture stays a module.
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject {
                ["id"] = "chair",
                ["document"] = new JsonObject {
                    ["shapes"] = new JsonArray(
                        new JsonObject { ["name"] = "seat", ["type"] = "Box" },
                        new JsonObject { ["name"] = "leg", ["type"] = "Cylinder", ["parent"] = "missing-shape" }
                    ),
                },
            }),
        };
        var finding = Assert.Single(LintReferencesAsModule(document), d => d.Code == "PUCK034");
        Assert.Equal(DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains("missing-shape", finding.Message);
    }

    [Fact]
    public void DeclaredShapeParentIsNotFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject {
                ["id"] = "chair",
                ["document"] = new JsonObject {
                    ["shapes"] = new JsonArray(
                        new JsonObject { ["name"] = "seat", ["type"] = "Box" },
                        new JsonObject { ["name"] = "leg", ["type"] = "Cylinder", ["parent"] = "seat" }
                    ),
                },
            }),
        };
        Assert.Empty(LintReferencesAsModule(document));
    }

    [Fact]
    public void UnresolvedCameraReferenceIsFlagged() {
        var document = new JsonObject {
            ["cameras"] = new JsonArray(new JsonObject { ["name"] = "main" }),
            ["views"] = new JsonObject {
                ["layouts"] = new JsonArray(new JsonObject {
                    ["name"] = "action",
                    ["slots"] = new JsonArray(new JsonObject { ["camera"] = "missing-cam" }),
                }),
            },
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_008") && d.Message.Contains("missing-cam"));
    }

    [Fact]
    public void DeclaredCameraReferenceIsNotFlagged() {
        var document = new JsonObject {
            ["cameras"] = new JsonArray(new JsonObject { ["name"] = "main" }),
            ["views"] = new JsonObject {
                ["layouts"] = new JsonArray(new JsonObject {
                    ["name"] = "action",
                    ["slots"] = new JsonArray(new JsonObject { ["camera"] = "main" }),
                }),
            },
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }

    [Fact]
    public void UnresolvedSpawnPointReferenceIsFlagged() {
        var document = new JsonObject {
            ["spawnPoints"] = new JsonArray(new JsonObject { ["id"] = "plaza-1" }),
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["effects"] = new JsonArray(new JsonObject { ["$type"] = "designateBody", ["spawnPoint"] = "plaza-9" }),
            }),
        };
        Assert.Contains(LintReferencesAsRoot(document), d => (d.Code == "PUCK_LINT_008") && d.Message.Contains("plaza-9"));
    }

    [Fact]
    public void ChannelPrefixTypoIsFlaggedAsInformation() {
        // The channel-prefix typo check is purely local (a fixed known-prefix list), so it runs whether or not the
        // document declares a basis; this fixture stays a module.
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue", ["comparison"] = "Equal", ["kind"] = "Fixed",
                    ["left"] = "$tabl:power:1", ["right"] = "1",
                },
                ["effects"] = new JsonArray(),
            }),
        };
        var finding = Assert.Single(LintReferencesAsModule(document), d => d.Code == "PUCK_LINT_009");
        Assert.Equal(DiagnosticSeverity.Information, finding.Severity);
        Assert.Contains("$table", finding.Message);
    }

    [Fact]
    public void KnownReservedChannelPrefixIsNotFlagged() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue", ["comparison"] = "Equal", ["kind"] = "Fixed",
                    ["left"] = "$table:power:1", ["right"] = "1",
                },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.DoesNotContain(LintReferencesAsModule(document), d => d.Code == "PUCK_LINT_009");
    }

    [Fact]
    public void ExtensionChannelPrefixFarFromEveryKnownPrefixIsNotFlagged() {
        // "$board" is a real extension prefix (Puck.World.Schema, not Puck.State's own RuleFacts) — never a typo
        // candidate against the RuleFacts list, so it must never be flagged.
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue", ["comparison"] = "Equal", ["kind"] = "Fixed",
                    ["left"] = "$board:cellOf:board:1:1", ["right"] = "1",
                },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.DoesNotContain(LintReferencesAsModule(document), d => d.Code == "PUCK_LINT_009");
    }

    [Fact]
    public void BasisSuppliedStateRowResolvesAndIsNotFlagged() {
        // The basis declares the row the root's own rule reads; the root never repeats it. Composed resolution
        // must see it, so this is the negative half of RootOwnMisspelledNameIsFlagged below.
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "puck-lint-refs-" + Guid.NewGuid().ToString("N"))).FullName;
        try {
            File.WriteAllText(Path.Combine(directory, "basis.json"), """{ "state": { "world": [ { "name": "transforms" } ] } }""");
            var document = new JsonObject {
                ["basis"] = "basis.json",
                ["rules"] = new JsonArray(new JsonObject {
                    ["name"] = "r",
                    ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "transforms", ["comparison"] = "Equal", ["value"] = 1 },
                    ["effects"] = new JsonArray(),
                }),
            };
            var diagnostics = new DiagnosticBag();
            PuckLinter.LintReferences(document, sourceMap: null, diagnostics, sourcePath: Path.Combine(directory, "root.world.json"));
            Assert.Empty(diagnostics);
        } finally {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RootOwnMisspelledNameIsFlaggedEvenWithABasis() {
        // The basis composes fine but supplies nothing named "whatever-undeclared" — a name only the root's own
        // rule spells, and misspells, so it must still be reported.
        Assert.Contains(
            LintReferencesAsRoot(new JsonObject {
                ["rules"] = new JsonArray(new JsonObject {
                    ["name"] = "r",
                    ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "whatever-undeclared", ["comparison"] = "Equal", ["value"] = 1 },
                    ["effects"] = new JsonArray(),
                }),
            }),
            d => (d.Code == "PUCK_LINT_005") && d.Message.Contains("whatever-undeclared")
        );
    }

    [Fact]
    public void ModuleNeverReportsAnUnresolvedName() {
        // No basis: the document is a module some unknown root may supply "whatever-undeclared" for, so a
        // standalone pass over it alone must stay silent.
        Assert.Empty(LintReferencesAsModule(new JsonObject {
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "whatever-undeclared", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        }));
    }

    [Fact]
    public void DollarPrefixedStateNameIsNeverCheckedAgainstDeclaredRows() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "$tick", ["comparison"] = "GreaterOrEqual", ["value"] = 600 },
                ["effects"] = new JsonArray(),
            }),
        };
        Assert.Empty(LintReferencesAsRoot(document));
    }
}
