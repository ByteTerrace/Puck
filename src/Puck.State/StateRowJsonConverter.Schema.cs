using System.Text.Json.Nodes;

namespace Puck.State;

public abstract partial class StateRowJsonConverter<TRow> {
    /// <summary>One extra top-level member a derived row claims, alongside the schema it validates against — the
    /// schema counterpart of <see cref="ClaimsMember"/>.</summary>
    /// <param name="Name">The wire member name.</param>
    /// <param name="Schema">The member's schema.</param>
    protected readonly record struct SchemaClaimedMember(string Name, JsonNode Schema);

    /// <summary>Gets the extra top-level members a derived row claims — the schema counterpart of
    /// <see cref="ClaimsMember"/>.</summary>
    /// <param name="exportType">Exports another type's schema through the same exporter.</param>
    protected virtual IReadOnlyList<SchemaClaimedMember> SchemaClaimedMembers(Func<Type, JsonNode> exportType) => [];

    /// <summary>Gets the claimed members that satisfy <see cref="DeclaresDrawSite"/> — admitting <c>drawCursor</c>/
    /// <c>drawnMasks</c> without an authored <c>draw</c> facet.</summary>
    protected virtual IReadOnlyList<string> SchemaDrawSiteMembers => [];
    /// <summary>Gets the claimed members refused alongside <c>cycle</c>, beyond the shared shape's own draw/advance/
    /// dynamics/capacity rules — the schema counterpart of the derived <see cref="Validate"/> override.</summary>
    protected virtual IReadOnlyList<string> SchemaCycleExclusiveMembers => [];

    // Every pair of top-level members Read() refuses when both are present — the shared shape's own rules, in
    // declaration order. A derived row's own cross-exclusions ride SchemaCycleExclusiveMembers instead, since the
    // member name itself is derived-specific.
    private static readonly (string First, string Second)[] SharedExclusivePairs = [
        ("value", "cells"),
        ("value", "capacity"),
        ("advance", "draw"),
        ("dynamics", "draw"),
        ("dynamics", "advance"),
        ("cycle", "draw"),
        ("cycle", "advance"),
        ("cycle", "dynamics"),
    ];

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        var claimed = SchemaClaimedMembers(exportType: exportType);
        var properties = new JsonObject {
            ["name"] = exportType(typeof(CellName)),
            ["kind"] = exportType(typeof(CellKind)),
            ["value"] = KindConditionalValueSchema(),
            ["cells"] = new JsonObject {
                ["type"] = "array",
                ["items"] = CellSchema(exportType: exportType),
            },
            ["min"] = KindConditionalEnvelopeSchema(),
            ["max"] = KindConditionalEnvelopeSchema(),
            ["capacity"] = new JsonObject { ["type"] = "integer" },
            ["overflow"] = exportType(typeof(StateOverflow)),
            ["evicts"] = new JsonObject { ["type"] = "boolean" },
            ["advance"] = exportType(typeof(StateAdvance)),
            ["draw"] = exportType(typeof(Draw)),
            ["drawCursor"] = new JsonObject { ["type"] = "integer" },
            ["drawnMasks"] = new JsonObject {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9a-fA-F]{64}$" },
            },
            ["historyCursor"] = new JsonObject { ["type"] = "integer" },
            ["visibility"] = exportType(typeof(StateVisibility)),
            ["knowledge"] = exportType(typeof(StateKnowledge)),
            ["phase"] = exportType(typeof(StatePhase)),
            ["phaseOf"] = new JsonObject { ["type"] = "string" },
            ["valuesFrom"] = new JsonObject { ["type"] = "string" },
            ["domain"] = exportType(typeof(StateDomain)),
            ["inverse"] = exportType(typeof(StateInverse)),
            ["dynamics"] = exportType(typeof(StateDynamics)),
            ["cycle"] = exportType(typeof(StateCycle)),
            ["clock"] = ClockSchema(),
            ["space"] = new JsonObject { ["type"] = "string" },
            ["enum"] = new JsonObject { ["type"] = "string" },
        };

        foreach (var member in claimed) {
            properties[member.Name] = member.Schema;
        }

        var allOf = new JsonArray();

        foreach (var kind in KindConditions) {
            allOf.Add(item: KindConditionBlock(kind: kind));
        }

        foreach (var (first, second) in SharedExclusivePairs) {
            allOf.Add(item: MutuallyExclusive(
                first: first,
                second: second
            ));
        }

        foreach (var extra in SchemaCycleExclusiveMembers) {
            allOf.Add(item: MutuallyExclusive(
                first: "cycle",
                second: extra
            ));
        }

        // A symbolic value domain rides an Int row alone.
        allOf.Add(item: new JsonObject {
            ["if"] = new JsonObject { ["required"] = new JsonArray("enum") },
            ["then"] = KindCondition(kind: nameof(CellKind.Int)),
        });
        allOf.Add(item: DrawSiteDependency());
        // 'clock' is the slot cell's own timing state (see StateCellClock) and rides beside 'value' alone.
        allOf.Add(item: new JsonObject {
            ["if"] = new JsonObject { ["required"] = new JsonArray("clock") },
            ["then"] = new JsonObject { ["required"] = new JsonArray("value") },
        });

        return new JsonObject {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(
            "name",
            "kind"
        ),
            ["additionalProperties"] = false,
            ["allOf"] = allOf,
        };
    }

    // The four CellKind tokens, spelled as StrictEnumConverter<CellKind> reads/writes them (the exact declared
    // member name).
    private static readonly string[] KindConditions = [
        nameof(CellKind.Int),
        nameof(CellKind.Fixed),
        nameof(CellKind.Bool),
        nameof(CellKind.Text),
        nameof(CellKind.Vector),
    ];

    // ReadCell's own switch: Text reads/writes a string, Bool a boolean, Fixed the decimal FixedQ4816 spelling, and
    // Int (the default arm) a plain JSON integer — applied to "value" and every "cells[].value" alike, since both
    // read through the same per-kind switch.
    private static JsonObject ValueSchemaFor(string kind) => (kind switch {
        nameof(CellKind.Text) => new JsonObject { ["type"] = "string" },
        nameof(CellKind.Vector) => new JsonObject { ["type"] = "string", ["description"] = "Unpadded base64url encoded signed 8-bit vector components." },
        nameof(CellKind.Bool) => new JsonObject { ["type"] = "boolean" },
        nameof(CellKind.Fixed) => new JsonObject { ["type"] = "string", ["description"] = "The decimal FixedQ4816 spelling (e.g. \"12.5\"), never raw bits." },
        _ => new JsonObject { ["type"] = "integer" },
    });
    // RequireNumeric's own switch: Fixed reads/writes the decimal FixedQ4816 spelling, every other kind (including
    // Bool and Text, which never author an envelope in practice) a plain JSON integer.
    private static JsonObject EnvelopeSchemaFor(string kind) => ((kind == nameof(CellKind.Fixed))
        ? new JsonObject { ["type"] = "string", ["description"] = "The decimal FixedQ4816 spelling (e.g. \"12.5\"), never raw bits." }
        : new JsonObject { ["type"] = "integer" }
    );
    private static JsonObject KindCondition(string kind) => new() {
        ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = kind } },
        ["required"] = new JsonArray("kind"),
    };
    // Every occurrence a row's own "kind" governs the representation of: the slot "value" sugar, the envelope
    // bounds, and each keyed cell's own "value" — narrowed together so a document authoring the wrong kind's
    // representation anywhere in the row fails the same conditional.
    private static JsonObject KindConditionBlock(string kind) {
        if (kind == nameof(CellKind.Vector)) {
            return new JsonObject {
                ["if"] = KindCondition(kind: kind),
                ["then"] = new JsonObject {
                    ["properties"] = new JsonObject {
                        ["value"] = ValueSchemaFor(kind: kind),
                        ["cells"] = new JsonObject {
                            ["items"] = new JsonObject {
                                ["properties"] = new JsonObject { ["value"] = ValueSchemaFor(kind: kind) },
                            },
                        },
                    },
                    ["not"] = new JsonObject {
                        ["anyOf"] = new JsonArray(
                            new JsonObject { ["required"] = new JsonArray("min") },
                            new JsonObject { ["required"] = new JsonArray("max") }
                        ),
                    },
                },
            };
        }

        return new JsonObject {
            ["if"] = KindCondition(kind: kind),
            ["then"] = new JsonObject {
                ["properties"] = new JsonObject {
                    ["value"] = ValueSchemaFor(kind: kind),
                    ["min"] = EnvelopeSchemaFor(kind: kind),
                    ["max"] = EnvelopeSchemaFor(kind: kind),
                    ["cells"] = new JsonObject {
                        ["items"] = new JsonObject {
                            ["properties"] = new JsonObject { ["value"] = ValueSchemaFor(kind: kind) },
                        },
                    },
                },
            },
        };
    }
    // A row's own "value"/"cells[].value" is authored in one of four representations, resolved by the sibling
    // "kind" member — see KindConditionBlock. Absent a fixed kind at this node (kind lives one level up, on the
    // enclosing row), the bare member accepts any one of the four; the row-level allOf narrows it precisely.
    private static JsonObject KindConditionalValueSchema() => new() {
        ["anyOf"] = new JsonArray(
        new JsonObject { ["type"] = "integer" },
        new JsonObject { ["type"] = "string" },
        new JsonObject { ["type"] = "boolean" }
    ),
    };
    private static JsonObject KindConditionalEnvelopeSchema() => new() {
        ["anyOf"] = new JsonArray(
        new JsonObject { ["type"] = "integer" },
        new JsonObject { ["type"] = "string" }
    ),
    };
    // ReadClock's own shape: every member optional (a fresh cell has no "clock" at all), the fixed-native "y0"/"v0"
    // (the decimal FixedQ4816 spelling regardless of the carrying row's own kind) among them. Never delegated to a
    // nested type's own converter — StateCellClock carries no JSON contract of its own, only this converter's
    // hand-rolled Read/Write.
    private static JsonObject ClockSchema() => new() {
        ["type"] = "object",
        ["properties"] = new JsonObject {
            ["epochTick"] = new JsonObject { ["type"] = "integer" },
            ["epochEngineTick"] = new JsonObject { ["type"] = "integer" },
            ["y0"] = new JsonObject { ["type"] = "string" },
            ["v0"] = new JsonObject { ["type"] = "string" },
            ["substepTicks"] = new JsonObject { ["type"] = "integer" },
        },
        ["additionalProperties"] = false,
    };
    // ReadCells' own per-entry shape: "key"/"value" required, every other member optional and read through the
    // options' resolver (advance/dynamics/cycle/visibility/observation) or hand-rolled (clock — see ClockSchema).
    private static JsonObject CellSchema(Func<Type, JsonNode> exportType) => new() {
        ["type"] = "object",
        ["properties"] = new JsonObject {
            ["key"] = new JsonObject { ["type"] = "string" },
            ["value"] = KindConditionalValueSchema(),
            ["advance"] = exportType(typeof(StateAdvance)),
            ["dynamics"] = exportType(typeof(StateDynamics)),
            ["cycle"] = exportType(typeof(StateCycle)),
            ["behavior"] = exportType(typeof(StateCellBehavior)),
            ["clock"] = ClockSchema(),
            ["visibility"] = exportType(typeof(StateVisibility)),
            ["observation"] = exportType(typeof(StateObservation)),
            ["provenance"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray(
        "key",
        "value"
    ),
        ["additionalProperties"] = false,
        // A cell opting out with behavior="none" declares no trait of its own either — see EffectiveBehavior.Resolve.
        ["allOf"] = new JsonArray(new JsonObject {
            ["if"] = new JsonObject {
                ["required"] = new JsonArray("behavior"),
                ["properties"] = new JsonObject { ["behavior"] = new JsonObject { ["const"] = nameof(StateCellBehavior.None) } },
            },
            ["then"] = new JsonObject {
                ["not"] = new JsonObject {
                    ["anyOf"] = new JsonArray(
                new JsonObject { ["required"] = new JsonArray("advance") },
                new JsonObject { ["required"] = new JsonArray("dynamics") },
                new JsonObject { ["required"] = new JsonArray("cycle") }
            ),
                },
            },
        }),
    };
    private static JsonObject MutuallyExclusive(string first, string second) => new() {
        ["not"] = new JsonObject {
            ["allOf"] = new JsonArray(
        new JsonObject { ["required"] = new JsonArray(first) },
        new JsonObject { ["required"] = new JsonArray(second) }
    ),
        },
    };
    // drawCursor/drawnMasks are engine bookkeeping for a draw site alone — an authored "draw" facet, or one of the
    // derived row's own SchemaDrawSiteMembers (DeclaresDrawSite's schema counterpart).
    private JsonObject DrawSiteDependency() {
        var sites = new List<JsonNode> { new JsonObject { ["required"] = new JsonArray("draw") } };

        foreach (var member in SchemaDrawSiteMembers) {
            sites.Add(item: new JsonObject { ["required"] = new JsonArray(member) });
        }

        return new JsonObject {
            ["if"] = new JsonObject { ["not"] = new JsonObject { ["anyOf"] = new JsonArray(items: [.. sites]) } },
            ["then"] = new JsonObject {
                ["not"] = new JsonObject {
                    ["anyOf"] = new JsonArray(
            new JsonObject { ["required"] = new JsonArray("drawCursor") },
            new JsonObject { ["required"] = new JsonArray("drawnMasks") }
        ),
                },
            },
        };
    }
}
