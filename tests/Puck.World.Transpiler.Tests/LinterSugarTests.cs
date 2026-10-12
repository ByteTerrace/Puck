using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><see cref="PuckLinter.LintReferences"/> — the symbol-resolution pass over lowered JSON — exercised
/// against every shipped world's decompiled form, plus a fixture per diagnostic code and per basis/module mode.</summary>
public class LinterSugarTests {
    // Leaves `document` basis-less, so it lints as a module (`resolveGlobalReferences: false`) — the sourcePath
    // is never touched, since a module composes nothing.
    private static DiagnosticBag LintReferencesAsModule(JsonObject document) {
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            document,
            sourceMap: null,
            diagnostics,
            sourcePath: "module.world.json"
        );
        return diagnostics;
    }
    // ---- Fixtures: one deliberately-broken and one deliberately-valid case per new rule ------------------------

    // Turns `document` into a root: gives it a `basis` naming a trivial, empty basis file in a scratch directory,
    // so composed-catalog resolution runs (`resolveGlobalReferences: true`) without depending on any real shipped
    // basis content.
    private static DiagnosticBag LintReferencesAsRoot(JsonObject document) {
        using var directory = new TemporaryDirectory(prefix: "puck-lint-refs-");

        _ = directory.WriteText(
            name: "basis.world.json",
            text: "{}"
        );
        document["basis"] = "basis";
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            document,
            sourceMap: null,
            diagnostics,
            sourcePath: directory.PathOf(name: "root.world.json")
        );
        return diagnostics;
    }

    [Fact]
    public void ANonCompareValueLeftRightPairIsNeverReadAsAnExpression() {
        // WorldInteraction's own "left"/"right" name a property or placement id, never a ExpressionProgram — only
        // ActionPredicate.CompareValue's pair is. A bare identifier there must never be checked against `state`.
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["interactions"] = new JsonObject {
                ["interactions"] = new JsonArray(new JsonObject {
                    ["name"] = "i",
                    ["left"] = WorldExpressionJson.Node(text: "hound"),
                    ["right"] = WorldExpressionJson.Node(text: "bone"),
                    ["coOccurrence"] = "Region",
                    ["range"] = 0,
                    ["effects"] = new JsonArray(),
                }),
            },
        };

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
    }
    [Fact]
    public void BasisSuppliedStateRowResolvesAndIsNotFlagged() {
        // The basis declares the row the root's own rule reads; the root never repeats it. Composed resolution
        // must see it, so this is the negative half of RootOwnMisspelledNameIsFlagged below.
        using var directory = new TemporaryDirectory(prefix: "puck-lint-refs-");

        _ = directory.WriteText(
            name: "basis.world.json",
            text: """{ "state": { "world": [ { "name": "transforms" } ] } }"""
        );
        var document = new JsonObject {
            ["basis"] = "basis",
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "transforms", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        };
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            document,
            sourceMap: null,
            diagnostics,
            sourcePath: directory.PathOf(name: "root.world.json")
        );
        Assert.Empty(collection: diagnostics);
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
                    ["$type"] = "compareValue",
                    ["comparison"] = "Equal",
                    ["kind"] = "Fixed",
                    ["left"] = WorldExpressionJson.Node(text: "$tabl:power:1"),
                    ["right"] = WorldExpressionJson.Node(text: "1"),
                },
                ["effects"] = new JsonArray(),
            }),
        };
        var finding = Assert.Single(
            collection: LintReferencesAsModule(document: document),
            predicate: d => (d.Code == "PUCK_LINT_009")
        );

        Assert.Equal(
            DiagnosticSeverity.Information,
            finding.Severity
        );
        Assert.Contains(
            "$table",
            finding.Message
        );
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

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
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

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
    }
    [Fact]
    public void DeclaredPrototypeIdIsNotFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair" }),
            },
        };

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
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

        Assert.Empty(collection: LintReferencesAsModule(document: document));
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

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
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

        Assert.Empty(collection: LintReferencesAsRoot(document: document));
    }
    // "$board" is a real extension prefix (Puck.World.Schema, not Puck.State's own RuleFacts) and "$table" a known
    // reserved one: neither is ever a typo candidate against the RuleFacts list.
    [InlineData("$board:cellOf:board:1:1")]
    [InlineData("$table:power:1")]
    [Theory]
    public void AKnownChannelPrefixIsNotFlagged(string channel) {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray() },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue",
                    ["comparison"] = "Equal",
                    ["kind"] = "Fixed",
                    ["left"] = WorldExpressionJson.Node(text: channel),
                    ["right"] = WorldExpressionJson.Node(text: "1"),
                },
                ["effects"] = new JsonArray(),
            }),
        };

        Assert.DoesNotContain(
            collection: LintReferencesAsModule(document: document),
            filter: d => (d.Code == "PUCK_LINT_009")
        );
    }
    [Fact]
    public void ModuleNeverReportsAnUnresolvedName() {
        // No basis: the document is a module some unknown root may supply "whatever-undeclared" for, so a
        // standalone pass over it alone must stay silent.
        Assert.Empty(collection: LintReferencesAsModule(document: new JsonObject {
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "whatever-undeclared", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        }));
    }
    [Fact]
    public void RootOwnMisspelledNameIsFlaggedEvenWithABasis() {
        // The basis composes fine but supplies nothing named "whatever-undeclared" — a name only the root's own
        // rule spells, and misspells, so it must still be reported.
        Assert.Contains(
            collection: LintReferencesAsRoot(document: new JsonObject {
                ["rules"] = new JsonArray(new JsonObject {
                    ["name"] = "r",
                    ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "whatever-undeclared", ["comparison"] = "Equal", ["value"] = 1 },
                    ["effects"] = new JsonArray(),
                }),
            }),
            filter: d => ((d.Code == "PUCK_LINT_005") && d.Message.Contains(value: "whatever-undeclared"))
        );
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

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_008") && d.Message.Contains(value: "missing-cam"))
        );
    }
    [Fact]
    public void UnresolvedPlacementParentIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "chair", ["parent"] = "missing-parent" }),
            },
        };

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_007") && d.Message.Contains(value: "missing-parent"))
        );
    }
    [Fact]
    public void UnresolvedPrototypeIdIsFlagged() {
        var document = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "p1", ["prototypeId"] = "table" }),
            },
        };

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_006") && d.Message.Contains(value: "table"))
        );
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
        var finding = Assert.Single(
            collection: LintReferencesAsModule(document: document),
            predicate: d => (d.Code == "PUCK034")
        );

        Assert.Equal(
            DiagnosticSeverity.Warning,
            finding.Severity
        );
        Assert.Contains(
            "missing-shape",
            finding.Message
        );
    }
    [Fact]
    public void UnresolvedSpawnPointReferenceIsFlagged() {
        var document = new JsonObject {
            ["spawnPoints"] = new JsonArray(new JsonObject { ["id"] = "plaza-1" }),
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["effects"] = new JsonArray(new JsonObject { ["$type"] = "designate", ["spawnPoint"] = "plaza-9" }),
            }),
        };

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_008") && d.Message.Contains(value: "plaza-9"))
        );
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

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_005") && d.Message.Contains(value: "hpp"))
        );
    }
    [Fact]
    public void UnresolvedStateTokenInsideACompareValueOperandIsFlagged() {
        var document = new JsonObject {
            ["state"] = new JsonObject { ["world"] = new JsonArray(new JsonObject { ["name"] = "hp" }) },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject {
                    ["$type"] = "compareValue",
                    ["comparison"] = "NotEqual",
                    ["kind"] = "Int",
                    ["left"] = WorldExpressionJson.Node(text: "hp + 1"),
                    ["right"] = WorldExpressionJson.Node(text: "mana"),
                },
                ["effects"] = new JsonArray(),
            }),
        };

        Assert.Contains(
            collection: LintReferencesAsRoot(document: document),
            filter: d => ((d.Code == "PUCK_LINT_005") && d.Message.Contains(value: "mana"))
        );
    }
}
