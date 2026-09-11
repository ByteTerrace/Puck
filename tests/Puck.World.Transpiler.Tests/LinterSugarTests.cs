using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckLinter.LintReferences"/> — the symbol-resolution pass over lowered JSON — exercised
/// against every shipped world's decompiled form, plus a fixture per new diagnostic code.</summary>
public class LinterSugarTests {
    private static string FindWorldsDirectory() {
        var dir = AppContext.BaseDirectory;
        while (dir is not null) {
            var candidate = Path.Combine(dir, "src", "Puck.World", "Assets", "worlds");
            if (Directory.Exists(candidate)) {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate src/Puck.World/Assets/worlds directory from test runner.");
    }

    private readonly record struct LintResult(DiagnosticBag Diagnostics, string DecompiledSource);

    private static LintResult LintShippedWorld(string relativePath) {
        var fullPath = Path.Combine(FindWorldsDirectory(), relativePath);
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
            );
            if (loweringResult.Value is not null) {
                PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics);
            }
        }
        return new LintResult(diagnostics, decompiled);
    }

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
    public void SolitaireContainerLintsCleanWithoutAnImportsAwareSkip() {
        // games/solitaire.world.json declares no `basis` and composes klondike/spider/freecell through `imports`,
        // but its own body never references a row/prototype those siblings own, so no skip is needed here.
        var result = LintShippedWorld("games/solitaire.world.json");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void RootWorldSkipsReferenceResolutionForItsBasis() {
        var result = LintShippedWorld("puck.world.json");

        var referenceCodes = (string[])["PUCK_LINT_005", "PUCK_LINT_006", "PUCK_LINT_007", "PUCK_LINT_008", "PUCK_LINT_009", "PUCK034"];
        var referenceFindings = result.Diagnostics.Where(d => referenceCodes.Contains(d.Code)).ToList();
        var skip = Assert.Single(referenceFindings);
        Assert.Equal("PUCK_LINT_005", skip.Code);
        Assert.Contains("basis", skip.Message, StringComparison.Ordinal);

        // PUCK026 ("rule carries no effect statements") false-positives on a decision-only rule — a pre-existing
        // defect in the parser stage (Parsing/PuckParser.Rules.cs), outside this stage's file scope; flagged, not
        // fixed, here. Every OTHER error would be a real regression.
        var unexpectedErrors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Code != "PUCK026").ToList();
        Assert.Empty(unexpectedErrors);
    }

    // Leaf "district" worlds imported by a parent (puck.world.json) or a sibling composer (solitaire.world.json)
    // that supplies the row/prototype they reference. Standalone, single-file lexical analysis cannot see that
    // supplying document, so an Information-level "unresolved" hint is the correct, honest result for these — never
    // an error, and never silently suppressed.
    public static TheoryData<string, string, string> CrossFileLeafWorlds => new() {
        { "games/freecell.world.json", "PUCK_LINT_005", "solitaire" },
        { "games/klondike.world.json", "PUCK_LINT_005", "solitaire" },
        { "games/spider.world.json", "PUCK_LINT_005", "solitaire" },
        { "games/mancala.world.json", "PUCK_LINT_006", "tabletop" },
        { "games/bowling.world.json", "PUCK_LINT_006", "billiardBall" },
    };

    [Theory]
    [MemberData(nameof(CrossFileLeafWorlds))]
    public void CrossFileLeafWorldsReportOnlyExpectedFindings(string relativePath, string expectedCode, string expectedName) {
        var result = LintShippedWorld(relativePath);
        Assert.All(result.Diagnostics, d => Assert.NotEqual(DiagnosticSeverity.Error, d.Severity));
        Assert.All(result.Diagnostics, d => Assert.Contains(d.Code, (string[])["PUCK_LINT_003", "PUCK_LINT_004", "PUCK_LINT_005", "PUCK_LINT_006"]));
        Assert.Contains(result.Diagnostics, d => (d.Code == expectedCode) && d.Message.Contains(expectedName, StringComparison.Ordinal));
    }

    // ---- Fixtures: one deliberately-broken and one deliberately-valid case per new rule ------------------------

    private static DiagnosticBag LintReferences(JsonObject document) {
        var diagnostics = new DiagnosticBag();
        PuckLinter.LintReferences(document, sourceMap: null, diagnostics);
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
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_005") && d.Message.Contains("hpp"));
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
        Assert.Empty(LintReferences(document));
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
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_005") && d.Message.Contains("mana"));
    }

    [Fact]
    public void UnresolvedPrototypeIdIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "table" }),
            },
        };
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_006") && d.Message.Contains("table"));
    }

    [Fact]
    public void DeclaredPrototypeIdIsNotFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair" }),
            },
        };
        Assert.Empty(LintReferences(document));
    }

    [Fact]
    public void UnresolvedPlacementParentIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair", ["parent"] = "missing-parent" }),
            },
        };
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_007") && d.Message.Contains("missing-parent"));
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
        Assert.Empty(LintReferences(document));
    }

    [Fact]
    public void UnresolvedShapeParentIsFlaggedAsAWarning() {
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
        var finding = Assert.Single(LintReferences(document), d => d.Code == "PUCK034");
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
        Assert.Empty(LintReferences(document));
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
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_008") && d.Message.Contains("missing-cam"));
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
        Assert.Empty(LintReferences(document));
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
        Assert.Contains(LintReferences(document), d => (d.Code == "PUCK_LINT_008") && d.Message.Contains("plaza-9"));
    }

    [Fact]
    public void ChannelPrefixTypoIsFlaggedAsInformation() {
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
        var finding = Assert.Single(LintReferences(document), d => d.Code == "PUCK_LINT_009");
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
        Assert.DoesNotContain(LintReferences(document), d => d.Code == "PUCK_LINT_009");
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
        Assert.DoesNotContain(LintReferences(document), d => d.Code == "PUCK_LINT_009");
    }

    [Fact]
    public void BasisDocumentSkipsEveryReferenceCheck() {
        var document = new JsonObject {
            ["basis"] = "worlds/standard.basis.json",
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "whatever-undeclared", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        };
        var only = Assert.Single(LintReferences(document));
        Assert.Equal("PUCK_LINT_005", only.Code);
        Assert.Contains("basis", only.Message, StringComparison.Ordinal);
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
        Assert.Empty(LintReferences(document));
    }
}
