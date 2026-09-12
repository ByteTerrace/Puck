using System.Text.Json.Nodes;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Emitter lowering for the `.puck` DSL sugar wave (§1-§5 of the sugar wave spec): gate classification,
/// effect-statement lowering, bind/decision/option, shape/placement row elision, and the unit-dimension table.</summary>
public class EmitterSugarTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);
        Assert.False(parseResult.Diagnostics.HasErrors, parseResult.Diagnostics.FormatReport(source));

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: diagnostics);
        return (loweringResult.Value!, diagnostics);
    }

    // JsonValue<T>.GetValue<T> refuses cross-numeric-type reads (long vs decimal vs double), and which CLR type an
    // emitted literal carries depends on whether it was integral — so assertions read every number through this.
    private static double AsDouble(JsonNode? node) => node switch {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<decimal>(out var m) => (double)m,
        _ => throw new InvalidOperationException($"'{node?.ToJsonString()}' is not a number"),
    };

    private static JsonObject FirstRule(string body) {
        var (json, diagnostics) = Lower(body);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(body));
        var rules = Assert.IsType<JsonArray>(json["rules"]);
        return Assert.IsType<JsonObject>(rules[0]);
    }

    // ---- §1 gate classification ------------------------------------------------------------------------------

    [Fact]
    public void BareComparisonLowersToCompareState() {
        var rule = FirstRule("""
            rule "r" {
                when settleHold == 60
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("settleHold", gate["state"]?.ToString());
        Assert.Equal("Equal", gate["comparison"]?.ToString());
        Assert.Equal(60, AsDouble(gate["value"]));
        Assert.False(gate.ContainsKey("key"));
    }

    [Fact]
    public void ComparandStateRowNeverFlipsOperandOrder() {
        var rule = FirstRule("""
            rule "r" {
                when houndIdentity[$each] != boneHolder[0]
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("houndIdentity", gate["state"]?.ToString());
        Assert.Equal("$each", gate["key"]?.ToString());
        Assert.Equal("NotEqual", gate["comparison"]?.ToString());
        Assert.Equal("boneHolder", gate["comparandState"]?.ToString());
        Assert.Equal("0", gate["comparandKey"]?.ToString());
    }

    [Fact]
    public void ConstantFirstComparisonFlipsToStateFirstCompareState() {
        var rule = FirstRule("""
            rule "r" {
                when 60 <= settleHold
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("settleHold", gate["state"]?.ToString());
        // `60 <= settleHold` flips to `settleHold >= 60` — LessOrEqual flips to GreaterOrEqual, not itself.
        Assert.Equal("GreaterOrEqual", gate["comparison"]?.ToString());
        Assert.Equal(60, AsDouble(gate["value"]));
    }

    [Fact]
    public void AndAndOrBuildFlatNaryLists() {
        var rule = FirstRule("""
            rule "r" {
                when a == 1 and b == 2 and c == 3
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("all", gate["$type"]?.ToString());
        var predicates = Assert.IsType<JsonArray>(gate["predicates"]);
        Assert.Equal(3, predicates.Count);
    }

    [Fact]
    public void NotWrapsItsOperand() {
        var rule = FirstRule("""
            rule "r" {
                when settleHold == 60 and not upright >= 1
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("all", gate["$type"]?.ToString());
        var predicates = Assert.IsType<JsonArray>(gate["predicates"]);
        var notNode = Assert.IsType<JsonObject>(predicates[1]);
        Assert.Equal("not", notNode["$type"]?.ToString());
        var inner = Assert.IsType<JsonObject>(notNode["predicate"]);
        Assert.Equal("compareState", inner["$type"]?.ToString());
        Assert.Equal("upright", inner["state"]?.ToString());
    }

    [Fact]
    public void ParenthesizedSubGateStaysOpaqueAndIsNeverFlattened() {
        var rule = FirstRule("""
            rule "r" {
                when a == 1 and (b == 2 or c == 3)
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("all", gate["$type"]?.ToString());
        var predicates = Assert.IsType<JsonArray>(gate["predicates"]);
        Assert.Equal(2, predicates.Count);
        var nested = Assert.IsType<JsonObject>(predicates[1]);
        Assert.Equal("any", nested["$type"]?.ToString());
    }

    [Fact]
    public void ExplicitKindSuffixForcesCompareValueEvenForTwoSimpleStateReads() {
        var rule = FirstRule("""
            rule "r" {
                when solitaireFreecell[from] != solitaireFreecell[to] : Int
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareValue", gate["$type"]?.ToString());
        Assert.Equal("NotEqual", gate["comparison"]?.ToString());
        Assert.Equal("Int", gate["kind"]?.ToString());
        Assert.Equal("solitaireFreecell[from]", gate["left"]?.ToString());
        Assert.Equal("solitaireFreecell[to]", gate["right"]?.ToString());
    }

    [Fact]
    public void MultiTokenOperandsForceCompareValueWithDefaultFixedKind() {
        var rule = FirstRule("""
            rule "r" {
                when a + b == c * 2
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareValue", gate["$type"]?.ToString());
        Assert.Equal("Fixed", gate["kind"]?.ToString());
        Assert.Equal("a + b", gate["left"]?.ToString());
        Assert.Equal("c * 2", gate["right"]?.ToString());
    }

    [Fact]
    public void LiveZoneBracketSelectorPassesThroughAsOneOpaqueOperand() {
        var rule = FirstRule("""
            rule "r" {
                when $zones[solitaireFreecell[from]][solitaireFreecell[card]] == 1
                effectRow = 1
            }
            """);

        var gate = Assert.IsType<JsonObject>(rule["gate"]);
        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("$zones[solitaireFreecell[from]]", gate["state"]?.ToString());
        // The trailing `[solitaireFreecell[card]]` is itself bracketed (not a bare name/number/backquoted name), so
        // ExpressionSpelling reads it as a live-cell-indirection key, not a literal key text — matching
        // freecell.world.json's own `"key": "$cell:solitaireFreecell:card"` for this exact source shape.
        Assert.Equal("$cell:solitaireFreecell:card", gate["key"]?.ToString());
    }

    // ---- §2 effect statements ----------------------------------------------------------------------------------

    [Fact]
    public void SetCellWithConstantRhsLowersToValue() {
        var rule = FirstRule("""
            rule "r" {
                hp[$each] = 5
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("setState", effect["$type"]?.ToString());
        Assert.Equal("hp", effect["state"]?.ToString());
        Assert.Equal("$each", effect["key"]?.ToString());
        Assert.Equal(5, AsDouble(effect["value"]));
    }

    [Fact]
    public void SetCellWithUnkeyedStateRhsLowersToBareFromState() {
        var rule = FirstRule("""
            rule "r" {
                boardHistory = $board:canonical:board
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("$board:canonical:board", effect["fromState"]?.ToString());
        Assert.False(effect.ContainsKey("fromKey"));
        Assert.False(effect.ContainsKey("expression"));
    }

    [Fact]
    public void SetCellWithKeyedStateRhsLowersToVerbatimExpressionNotFromStateFromKey() {
        // Confirmed against `first-witness` (puck.world.json), which ships
        // {"$type":"setState","expression":"houndIdentity[$each]","key":"0","state":"reporter"} for this exact
        // RHS shape — a keyed read never decomposes into fromState+fromKey, even though both spellings are
        // runtime-equivalent (§2.3). fromState+fromKey stay reachable through call-form.
        var rule = FirstRule("""
            rule "r" {
                reporter[0] = houndIdentity[$each]
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("houndIdentity[$each]", effect["expression"]?.ToString());
        Assert.False(effect.ContainsKey("fromState"));
        Assert.False(effect.ContainsKey("fromKey"));
    }

    [Fact]
    public void AddCellWithMultiTokenRhsLowersToVerbatimExpression() {
        var rule = FirstRule("""
            rule "r" {
                boneHolderTrust[$pair:each:cell:boneHolder:0] += (1 - boneHolderTrust[$pair:each:cell:boneHolder:0]) * 0.3
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("addState", effect["$type"]?.ToString());
        Assert.Equal("(1 - boneHolderTrust[$pair:each:cell:boneHolder:0]) * 0.3", effect["expression"]?.ToString());
        Assert.False(effect.ContainsKey("value"));
    }

    [Fact]
    public void SetCellStringRhsLowersToText() {
        var rule = FirstRule("""
            rule "r" {
                label = "ready"
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("ready", effect["text"]?.ToString());
    }

    [Fact]
    public void SetCellSecondsRhsLowersToValueSeconds() {
        var rule = FirstRule("""
            rule "r" {
                cooldown = 3s
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal(3, AsDouble(effect["valueSeconds"]));
        Assert.False(effect.ContainsKey("value"));
    }

    [Fact]
    public void PushHasNoKeyField() {
        var rule = FirstRule("""
            rule "r" {
                push boardHistory = $board:canonical:board
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("pushState", effect["$type"]?.ToString());
        Assert.Equal("boardHistory", effect["state"]?.ToString());
        Assert.Equal("$board:canonical:board", effect["fromState"]?.ToString());
        Assert.False(effect.ContainsKey("key"));
    }

    [Fact]
    public void CountdownRemoveAndScheduleLowerToTheirOwnShapes() {
        var rule = FirstRule("""
            rule "r" {
                countdown claimTicks
                remove promotionPending[0]
                schedule respawnAt in 5s
            }
            """);
        var effects = Assert.IsType<JsonArray>(rule["effects"]);

        var countdown = Assert.IsType<JsonObject>(effects[0]);
        Assert.Equal("countdownState", countdown["$type"]?.ToString());
        Assert.Equal("claimTicks", countdown["state"]?.ToString());

        var remove = Assert.IsType<JsonObject>(effects[1]);
        Assert.Equal("removeStateCell", remove["$type"]?.ToString());
        Assert.Equal("0", remove["key"]?.ToString());

        var schedule = Assert.IsType<JsonObject>(effects[2]);
        Assert.Equal("scheduleState", schedule["$type"]?.ToString());
        Assert.Equal(5, AsDouble(schedule["delaySeconds"]));
    }

    [Fact]
    public void TransformWrapsTheCallFormValue() {
        var rule = FirstRule("""
            rule "r" {
                transform t = boardCombine(left: "board", operation: Copy, row: "lastLegal")
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("transformState", effect["$type"]?.ToString());
        var transform = Assert.IsType<JsonObject>(effect["transform"]);
        Assert.Equal("boardCombine", transform["$type"]?.ToString());
        Assert.Equal("board", transform["left"]?.ToString());
        Assert.Equal("Copy", transform["operation"]?.ToString());
    }

    [Fact]
    public void TransactionCarriesEffectsAndOptionalOnFailure() {
        var rule = FirstRule("""
            rule "r" {
                transaction {
                    a = 1
                    b += 2
                } onFailure {
                    c = 0
                }
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("transaction", effect["$type"]?.ToString());
        Assert.Equal(2, Assert.IsType<JsonArray>(effect["effects"]).Count);
        Assert.Single(Assert.IsType<JsonArray>(effect["onFailure"]));
    }

    [Fact]
    public void TransactionWithNoOnFailureOmitsTheKeyEntirely() {
        var rule = FirstRule("""
            rule "r" {
                transaction {
                    a = 1
                }
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.False(effect.ContainsKey("onFailure"));
    }

    [Fact]
    public void CallFormEffectLowersLikeAnyOtherTypeObject() {
        var rule = FirstRule("""
            rule "r" {
                generate(row: "drawSite")
            }
            """);
        var effect = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["effects"])[0]);
        Assert.Equal("generate", effect["$type"]?.ToString());
        Assert.Equal("drawSite", effect["row"]?.ToString());
    }

    // ---- §3 bind / decision / option ---------------------------------------------------------------------------

    [Fact]
    public void BindCarriesNameKindAndVerbatimExpression() {
        var rule = FirstRule("""
            rule "r" {
                bind lostRooks: Int = ($board:mask:lastLegal:4:4 & ~$board:mask:board:4:4) & 0x81
                effectRow = 1
            }
            """);
        var bindings = Assert.IsType<JsonArray>(rule["bindings"]);
        var binding = Assert.IsType<JsonObject>(bindings[0]);
        Assert.Equal("lostRooks", binding["name"]?.ToString());
        Assert.Equal("Int", binding["kind"]?.ToString());
        Assert.Equal("($board:mask:lastLegal:4:4 & ~$board:mask:board:4:4) & 0x81", binding["expression"]?.ToString());
    }

    [Fact]
    public void DecisionElidesDefaultsAndAlwaysPrintsPeriodSeconds() {
        // A rule with only a `decision` body and no other effect statement still needs one authored entry effect —
        // PUCK026 fires without it even though Rule.Effects may be empty when a decision carries the selection.
        var rule = FirstRule("""
            rule "r" {
                forEach: "hound"
                claimTicks = 0
                decision {
                    periodSeconds: 1s
                    commitmentSeconds: 3s
                    seed: 11
                    option "follow" {
                        when hound[$right] == 1
                        score: trust * 2.0
                        designateBody(key: $each, kind: Body, register: companion, targetKey: $right)
                    }
                }
            }
            """);

        var decision = Assert.IsType<JsonObject>(rule["decision"]);
        Assert.Equal(1, AsDouble(decision["periodSeconds"]));
        Assert.Equal(3, AsDouble(decision["commitmentSeconds"]));
        Assert.Equal(11, AsDouble(decision["seed"]));
        // mode/scoreKind/incumbentBonus were never authored, so they equal WorldDecision's own C# defaults and elide.
        Assert.False(decision.ContainsKey("mode"));
        Assert.False(decision.ContainsKey("scoreKind"));
        Assert.False(decision.ContainsKey("incumbentBonus"));

        var options = Assert.IsType<JsonArray>(decision["options"]);
        var option = Assert.IsType<JsonObject>(options[0]);
        Assert.Equal("follow", option["name"]?.ToString());
        Assert.Equal("trust * 2.0", option["score"]?.ToString());
        var optionGate = Assert.IsType<JsonObject>(option["gate"]);
        Assert.Equal("compareState", optionGate["$type"]?.ToString());
        var optionEffects = Assert.IsType<JsonArray>(option["effects"]);
        var designate = Assert.IsType<JsonObject>(optionEffects[0]);
        Assert.Equal("designateBody", designate["$type"]?.ToString());
    }

    [Fact]
    public void DecisionInterruptAndOnNoChoiceLowerToTheirOwnFields() {
        var rule = FirstRule("""
            rule "r" {
                claimTicks = 0
                decision {
                    periodSeconds: 1s
                    interrupt hp <= 0
                    onNoChoice {
                        state = 0
                    }
                    option "idle" {
                        score: 1
                    }
                }
            }
            """);
        var decision = Assert.IsType<JsonObject>(rule["decision"]);
        var interrupt = Assert.IsType<JsonObject>(decision["interrupt"]);
        Assert.Equal("compareState", interrupt["$type"]?.ToString());
        var onNoChoice = Assert.IsType<JsonArray>(decision["onNoChoice"]);
        Assert.Single(onNoChoice);
    }

    // ---- §5 unit-dimension table --------------------------------------------------------------------------------

    [Fact]
    public void DegreesNativeFieldPassesThroughUnconverted() {
        var (json, diagnostics) = Lower("""
            placements {
                placement "p" {
                    prototype: proto
                    position: [0, 0, 0]
                    yawDegrees: 45deg
                }
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(json["placements"])["rows"]);
        var row = Assert.IsType<JsonObject>(rows[0]);
        Assert.Equal(45, AsDouble(row["yawDegrees"]));
    }

    [Fact]
    public void RadiansFieldConvertsDegreesAndPassesThroughRadians() {
        // `orbit`'s named-argument keys thread as the CallExpressionNode's own field keys regardless of the
        // enclosing property's own name, so this exercises the table without needing real seatRig semantics.
        var (json, diagnostics) = Lower("""
            host {
                camera: orbit(pitch: 90deg, yaw: 1rad, distance: 5)
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var orbit = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(json["host"])["camera"]);
        Assert.Equal(Math.Round(Math.PI / 2, 6), AsDouble(orbit["pitch"]), 6);
        Assert.Equal(1d, AsDouble(orbit["yaw"]));
    }

    [Fact]
    public void SecondsFieldConvertsMillisecondsToSeconds() {
        // `schedule ... in` itself requires the 's' suffix (PUCK010) — ms only reaches the ordinary
        // property/call-argument layer the unit-dimension table governs, so this exercises the conversion through
        // `periodSeconds` instead (§5's own worked example: `schedule respawnAt in 500ms` -> `delaySeconds: 0.5`
        // describes the *value*, not `schedule`'s own required-unit grammar).
        var (json, diagnostics) = Lower("""
            rule "r" {
                claimTicks = 0
                decision {
                    periodSeconds: 500ms
                    option "idle" {
                        score: 1
                    }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["rules"])[0]);
        var decision = Assert.IsType<JsonObject>(rule["decision"]);
        Assert.Equal(0.5, AsDouble(decision["periodSeconds"]));
    }

    [Fact]
    public void MetersFieldConvertsCentimetersAndMillimeters() {
        var (json, diagnostics) = Lower("""
            placements {
                placement "p" {
                    prototype: proto
                    position: [150cm, 0, 5mm]
                }
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var rows = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(json["placements"])["rows"]);
        var position = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(rows[0])["position"]);
        Assert.Equal(1.5, position[0]!.GetValue<double>());
        Assert.Equal(0.005, position[2]!.GetValue<double>());
    }

    [Fact]
    public void PercentSuffixDividesByOneHundredOnAnyField() {
        var (json, diagnostics) = Lower("""
            host {
                opacity: 50%
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var host = Assert.IsType<JsonObject>(json["host"]);
        Assert.Equal(0.5, host["opacity"]?.GetValue<double>());
    }

    [Fact]
    public void UnknownFieldWithUnitReportsPuck024() {
        var (_, diagnostics) = Lower("""
            host {
                width: 5m
            }
            """);
        Assert.Contains(diagnostics, d => d.Code == "PUCK024");
    }

    [Fact]
    public void KnownFieldWithWrongUnitReportsPuck025() {
        var (_, diagnostics) = Lower("""
            placements {
                placement "p" {
                    prototype: proto
                    yawDegrees: 5s
                }
            }
            """);
        Assert.Contains(diagnostics, d => d.Code == "PUCK025");
    }

    // ---- §4 shapes and placements ---------------------------------------------------------------------------

    [Fact]
    public void ShapeFillsBlendSmoothRotationScaleOnlyWhenAbsent() {
        var (json, diagnostics) = Lower("""
            shape Box "board" {
                position: [0, -2.5, 0]
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var shapes = Assert.IsType<JsonArray>(json["shapes"]);
        var shape = Assert.IsType<JsonObject>(shapes[0]);
        Assert.Equal("Box", shape["type"]?.ToString());
        Assert.Equal("board", shape["name"]?.ToString());
        Assert.Equal("Union", shape["blend"]?.ToString());
        Assert.Equal(0, AsDouble(shape["smooth"]));
        var rotation = Assert.IsType<JsonArray>(shape["rotation"]);
        Assert.Equal([0, 0, 0, 1], rotation.Select(static n => n!.GetValue<int>()));
        var scale = Assert.IsType<JsonArray>(shape["scale"]);
        Assert.Equal([1, 1, 1], scale.Select(static n => n!.GetValue<int>()));
    }

    // `group` is null (omitted from the wire JSON) on an ungrouped shape and an explicit value — including an
    // explicit 0 — on a grouped one; those are two different JSON shapes with no shared default, so the emitter
    // never fills it and an unauthored shape carries no `group` key at all.
    [Fact]
    public void ShapeNeverFillsGroupWhenAbsent() {
        var (json, diagnostics) = Lower("""
            shape Box "board" {
                position: [0, -2.5, 0]
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var shape = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["shapes"])[0]);
        Assert.False(shape.ContainsKey("group"));
    }

    [Fact]
    public void ShapeExplicitBlendAndRotationReferenceAreNeverOverwritten() {
        var (json, diagnostics) = Lower("""
            shape Cylinder "meadow" {
                blend: SmoothUnion
                rotation: "state.transforms.identity"
                position: [0, -2.5, 0]
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var shape = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["shapes"])[0]);
        Assert.Equal("SmoothUnion", shape["blend"]?.ToString());
        Assert.Equal("state.transforms.identity", shape["rotation"]?.ToString());
    }

    [Fact]
    public void ShapeWithNoNameLeavesTheBareTypeIdentifierAsType() {
        var (json, diagnostics) = Lower("""
            shape Sphere {
                position: [0, 0, 0]
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var shape = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(json["shapes"])[0]);
        Assert.Equal("Sphere", shape["type"]?.ToString());
        Assert.False(shape.ContainsKey("name"));
    }

    [Fact]
    public void PlacementBlockNameFillsIdNotNameAndPrototypeRenamesToPrototypeId() {
        var (json, diagnostics) = Lower("""
            placements {
                placement "debugRoom" {
                    prototype: debugRoom
                    parent: provingCourt
                    position: [0, 0, 0]
                    solid
                    grip: { holdable: true }
                }
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var placements = Assert.IsType<JsonObject>(json["placements"]);
        var rows = Assert.IsType<JsonArray>(placements["rows"]);
        var row = Assert.IsType<JsonObject>(rows[0]);

        Assert.Equal("debugRoom", row["id"]?.ToString());
        Assert.False(row.ContainsKey("name"));
        Assert.Equal("debugRoom", row["prototypeId"]?.ToString());
        Assert.False(row.ContainsKey("prototype"));
        Assert.Equal("provingCourt", row["parent"]?.ToString());
        Assert.Equal(0, AsDouble(row["yawDegrees"]));
        Assert.Equal(1, AsDouble(row["scale"]));
        var solid = Assert.IsType<JsonObject>(row["solid"]);
        Assert.Equal(0, AsDouble(solid["margin"]));
        var grip = Assert.IsType<JsonObject>(row["grip"]);
        Assert.True(grip["holdable"]?.GetValue<bool>());
    }

    [Fact]
    public void PlacementsPolicySurvivesAlongsidePlacementRows() {
        var (json, diagnostics) = Lower("""
            placements {
                policy: { maxLivePlacements: 8 }
                placement "p1" {
                    prototype: proto
                    position: [0, 0, 0]
                }
            }
            """);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(""));
        var placements = Assert.IsType<JsonObject>(json["placements"]);
        var policy = Assert.IsType<JsonObject>(placements["policy"]);
        Assert.Equal(8, AsDouble(policy["maxLivePlacements"]));
        Assert.Single(Assert.IsType<JsonArray>(placements["rows"]));
    }

    // ---- Shipped-world cross-checks --------------------------------------------------------------------------

    private static JsonObject LoadShippedRule(string relativeWorldPath, string ruleName) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(worldsDir, relativeWorldPath);
        Assert.True(File.Exists(fullPath), $"Shipped world file not found: {fullPath}");

        var root = JsonNode.Parse(File.ReadAllText(fullPath));
        var rules = Assert.IsType<JsonArray>(root!["rules"]);
        foreach (var candidate in rules) {
            var obj = Assert.IsType<JsonObject>(candidate);
            if (string.Equals(obj["name"]?.ToString(), ruleName, StringComparison.Ordinal)) {
                return obj;
            }
        }
        throw new InvalidOperationException($"rule '{ruleName}' not found in {relativeWorldPath}");
    }

    [Fact]
    public void HoundWitnessClaimGateMatchesTheShippedRule() {
        var expected = LoadShippedRule("puck.world.json", "witness-claim");

        var rule = FirstRule("""
            rule "witness-claim" {
                when boneHolder[0] >= 0 and houndIdentity[$each] != boneHolder[0] and $los:each:cell:boneHolder:0 == 1 and $distance:each:cell:boneHolder:0 <= 9
                mode: Edge
                forEach: "hound"
                boneHolderTrust[$pair:each:cell:boneHolder:0] += (1 - boneHolderTrust[$pair:each:cell:boneHolder:0]) * 0.3
            }
            """);

        var mismatch = JsonMismatch.Find(expected, rule, "witness-claim");
        Assert.Null(mismatch);
    }

    [Fact]
    public void ChessAdvanceTurnTransactionMatchesTheShippedRule() {
        var expected = LoadShippedRule("games/chess.world.json", "tabletop-advance-turn");

        var rule = FirstRule("""
            rule "tabletop-advance-turn" {
                when settleHold == 60 and verdict == 1 and move[kind] != 0
                mode: Edge
                bind lostRooks: Int = (($board:mask:lastLegal:4:4 & ~$board:mask:board:4:4) & 0x81) | (($board:mask:lastLegal:-4:-4 & ~$board:mask:board:-4:-4) & (0x81 << 56))
                bind lostKings: Int = (($board:mask:lastLegal:6:6 & ~$board:mask:board:6:6) & (1 << 4)) | (($board:mask:lastLegal:-6:-6 & ~$board:mask:board:-6:-6) & (1 << 60))
                transaction {
                    castleRights = castleRights | parallelBitExtract($bind:lostRooks, 0x81 | (0x81 << 56)) | (parallelBitDeposit(parallelBitExtract($bind:lostKings, (1 << 4) | (1 << 60)), 5) * 3)
                    transform t = boardCombine(left: "board", operation: Copy, row: "lastLegal")
                    previousInCheck[0] = inCheck[0]
                    previousInCheck[1] = inCheck[1]
                    enPassantTarget = (abs(move[mover]) == 1) & (abs(move[to] - move[from]) == 16) ? ((move[from] + move[to]) >> 1) : -1
                    turn = 1 - turn
                    promotionPending[0] = -1
                    promotionPending[1] = -1
                }
            }
            """);

        var mismatch = JsonMismatch.Find(expected, rule, "tabletop-advance-turn");
        Assert.Null(mismatch);
    }
}
