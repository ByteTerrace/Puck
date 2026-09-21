using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The export record validates against what a module declares, the compose-time check admits a reference
/// only through the export list the registry assigns the referencing field, and the registry assigns the facets
/// a host's kit, placement, rule, and HUD bindings bind through.</summary>
public sealed class WorldExportsLawTests {
    [InlineData("{\"$type\":\"release\",\"binding\":\"unit\"}")]
    [InlineData("{\"$type\":\"setState\",\"state\":\"unit.hp\",\"value\":1}")]
    [Theory]
    public void ReadExportDoesNotPermitLexicalPoolMutation(string effect) {
        var module = Host(body: """{"state":{"records":[{"name":"Unit","fields":[]}],"pools":[{"name":"units","record":"Unit","capacity":2}]}}""");
        var own = Host(body: $$"""{"rules":[{"name":"host","poolForEach":{"pool":"units","binding":"unit"},"effects":[{{effect}}]}]}""");
        var exports = new WorldExports(Reads: ["units"]);

        Assert.False(condition: WorldModuleExports.TryCheckLayers(hostPath: "host", ownBody: own, basis: [], imports: [("module", module, exports)], surfaces: out _, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "units");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "action");
        Assert.True(condition: WorldModuleExports.TryCheckLayers(hostPath: "host", ownBody: own, basis: [], imports: [("module", module, exports with { Actions = ["units"] })], surfaces: out _, reason: out reason), userMessage: reason);
    }
    [InlineData("{\"rules\":[{\"name\":\"host\",\"effects\":[{\"$type\":\"forEachPool\",\"pool\":\"units\",\"binding\":\"unit\",\"effects\":[{\"$type\":\"release\",\"binding\":\"unit\"}]}]}]}")]
    [InlineData("{\"interactions\":{\"interactions\":[{\"name\":\"host\",\"left\":\"units\",\"right\":\"units\",\"effects\":[{\"$type\":\"setState\",\"state\":\"left.hp\",\"value\":1}]}]}}")]
    [Theory]
    public void ReadExportDoesNotPermitNestedOrInteractionPoolMutation(string body) {
        var module = Host(body: """{"state":{"records":[{"name":"Unit","fields":[]}],"pools":[{"name":"units","record":"Unit","capacity":2}]}}""");

        Assert.False(condition: WorldModuleExports.TryCheckLayers(hostPath: "host", ownBody: Host(body: body), basis: [], imports: [("module", module, new WorldExports(Reads: ["units"]))], surfaces: out _, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "action");
    }

    private static JsonObject Host(string body) => ((JsonObject)JsonNode.Parse(json: body)!);
    private static JsonObject Module(string exports) => ((JsonObject)JsonNode.Parse(json: $$"""
        {
          "exports": {{exports}},
          "state": { "world": [ { "name": "request", "kind": "Int", "value": 0 }, { "name": "score", "kind": "Int", "value": 0 } ],
                     "lattices": [ { "$type": "grid", "name": "grid", "width": 2, "depth": 2, "cellSize": 1 } ] },
          "rules": [ { "name": "settle", "effects": [ { "$type": "setState", "state": "score", "fromState": "request" } ] } ]
        }
        """)!);
    private static WorldModuleSurface Take(JsonObject module) {
        Assert.True(
            condition: TryTake(
                module: module,
                reason: out var reason,
                surface: out var surface
            ),
            userMessage: reason
        );

        return surface!;
    }
    // Checks a host's own body against the module authored under Surface.
    private static bool TryCheck(JsonObject tree, string treeName, out string reason) {
        var module = Module(exports: Surface);

        Assert.True(
            condition: WorldModuleExports.TryTake(
                exports: out var exports,
                module: module,
                moduleName: "module",
                reason: out reason
            ),
            userMessage: reason
        );

        return WorldModuleExports.TryCheckLayers(
            hostPath: treeName,
            ownBody: tree,
            basis: [],
            imports: [("module", module, exports!)],
            surfaces: out _,
            reason: out reason
        );
    }
    private static bool TryTake(JsonObject module, out WorldModuleSurface? surface, out string reason) {
        surface = null;

        if (!WorldModuleExports.TryTake(
            exports: out var exports,
            module: module,
            moduleName: "module",
            reason: out reason
        )) {
            return false;
        }

        if (!WorldModuleExports.TryCheckLayers(
            hostPath: "host",
            ownBody: [],
            basis: [],
            imports: [("module", module, exports)],
            surfaces: out var surfaces,
            reason: out reason
        )) {
            return false;
        }

        surface = surfaces.Single();

        return true;
    }

    [Fact]
    public void AHostReferenceIsAdmittedOnlyThroughTheFacetTheRegistryAssignsItsField() {
        var surface = Take(module: Module(exports: Surface));

        // A rule effect drives an exported action; its gate reads an exported read; a HUD element binds an exported
        // binding; a key indirection reads through exports.reads.
        var admitted = Host(body: /*lang=json*/ """
            { "rules": [ { "name": "host", "gate": { "$type": "compareState", "state": "score", "comparison": "Less", "value": 3 },
                           "effects": [ { "$type": "addState", "state": "request", "value": 1 } ] } ],
              "hud": { "panels": [ { "id": "p", "layer": "Under", "style": "Strip", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 },
                       "elements": [ { "id": "e", "kind": "Text", "style": "Primary", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "template": "score {state.score}" } ] } ] } }
            """);

        Assert.True(
            condition: TryCheck(
                reason: out var admittedReason,
                tree: admitted,
                treeName: "host"
            ),
            userMessage: admittedReason
        );

        var writesARead = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "score", "value": 1 } ] } ] }""");

        Assert.False(condition: TryCheck(
            reason: out var writeReason,
            tree: writesARead,
            treeName: "host"
        ));
        Assert.Equal(
            actual: writeReason,
            expected: "host names 'score' at $.rules[0].effects[0].state, which module declares and does not export under exports.actions; a host binds only what its module exports."
        );

        var readsAnAction = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "own", "expression": "request + 1" } ] } ], "state": { "world": [ { "name": "own", "kind": "Int", "value": 0 } ] } }""");

        Assert.False(condition: TryCheck(
            reason: out var readReason,
            tree: readsAnAction,
            treeName: "host"
        ));
        Assert.Contains(
            actualString: readReason,
            expectedSubstring: "'request' at $.rules[0].effects[0].expression"
        );
        Assert.Contains(
            actualString: readReason,
            expectedSubstring: "exports.reads"
        );

        var bindsAPrivateRow = Host(body: /*lang=json*/ """{ "hud": { "panels": [ { "id": "p", "layer": "Under", "style": "Strip", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "elements": [ { "id": "e", "kind": "Gauge", "style": "Primary", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "binding": "state.request" } ] } ] } }""");

        Assert.False(condition: TryCheck(
            reason: out var bindReason,
            tree: bindsAPrivateRow,
            treeName: "host"
        ));
        Assert.Contains(
            actualString: bindReason,
            expectedSubstring: "'request' at $.hud.panels[0].elements[0].binding"
        );
        Assert.Contains(
            actualString: bindReason,
            expectedSubstring: "exports.bindings"
        );

        var keyIndirection = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "own", "key": "$cell:request:$value", "value": 1 } ] } ], "state": { "world": [ { "name": "own", "kind": "Int", "capacity": 4 } ] } }""");

        Assert.False(condition: TryCheck(
            reason: out var keyReason,
            tree: keyIndirection,
            treeName: "host"
        ));
        Assert.Contains(
            actualString: keyReason,
            expectedSubstring: "'request' at $.rules[0].effects[0].key"
        );

        // A module's own references, and a name the checked layer itself declares, are never checked.
        var selfReferencing = Module(exports: Surface);

        selfReferencing[propertyName: "rules"]!.AsArray().Add(value: JsonNode.Parse(json: /*lang=json*/ """{ "name": "reset", "effects": [ { "$type": "setState", "state": "score", "value": 0 } ] }"""));
        Assert.True(
            condition: WorldModuleExports.TryTake(
                exports: out var selfExports,
                module: selfReferencing,
                moduleName: "module",
                reason: out var selfReason
            ),
            userMessage: selfReason
        );
        Assert.True(
            condition: WorldModuleExports.TryCheckLayers(
                hostPath: "host",
                ownBody: [],
                basis: [],
                imports: [("module", selfReferencing, selfExports!)],
                surfaces: out _,
                reason: out selfReason
            ),
            userMessage: selfReason
        );

        var restates = Host(body: /*lang=json*/ """{ "state": { "world": [ { "name": "score", "kind": "Int", "value": 5 } ] }, "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "score", "value": 1 } ] } ] }""");

        Assert.True(condition: TryCheck(
            reason: out _,
            tree: restates,
            treeName: "host"
        ));
    }
    [Fact]
    public void TakingTheExportsStripsTheMemberAndValidatesEachEntry() {
        var module = Module(exports: /*lang=json*/ """{ "reads": [ "score", "grid" ], "actions": [ "request" ] }""");
        var surface = Take(module: module);

        Assert.False(condition: module.ContainsKey(propertyName: WorldExports.MemberName));
        Assert.True(condition: surface.Declares(name: "request"));
        Assert.True(condition: surface.Declares(name: "settle"));
        Assert.True(condition: surface.Admits(
            facet: WorldExportFacet.Read,
            name: "score"
        ));
        Assert.True(condition: surface.Admits(
            facet: WorldExportFacet.Read,
            name: "grid"
        ));
        Assert.True(condition: surface.Admits(
            facet: WorldExportFacet.Action,
            name: "request"
        ));
        Assert.False(condition: surface.Admits(
            facet: WorldExportFacet.Read,
            name: "request"
        ));
        Assert.False(condition: surface.Admits(
            facet: WorldExportFacet.Action,
            name: "score"
        ));
        Assert.False(condition: surface.Admits(
            facet: WorldExportFacet.Read,
            name: "settle"
        ));
        Assert.True(condition: surface.Admits(
            facet: WorldExportFacet.Action,
            name: "elsewhere"
        ));

        Assert.False(condition: TryTake(
            module: Module(exports: /*lang=json*/ """{ "reads": [ "winner" ] }"""),
            surface: out _,
            reason: out var undeclared
        ));
        Assert.Equal(
            actual: undeclared,
            expected: "host imports module: exports.reads names 'winner', which the module does not declare."
        );

        Assert.False(condition: TryTake(
            module: Module(exports: /*lang=json*/ """{ "writes": [ "request" ] }"""),
            surface: out _,
            reason: out var malformed
        ));
        Assert.Contains(
            actualString: malformed,
            expectedSubstring: "module: 'exports' is malformed"
        );

        Assert.True(condition: TryTake(
            module: Module(exports: "null"),
            surface: out var closed,
            reason: out _
        ));
        Assert.False(condition: closed!.Admits(
            facet: WorldExportFacet.Read,
            name: "score"
        ));
    }
    [Fact]
    public void TheRecordValidatesAgainstTheModulesDeclarations() {
        var declared = new HashSet<string>(
            collection: ["request", "score"],
            comparer: StringComparer.Ordinal
        );

        Assert.True(condition: new WorldExports(
            Reads: ["score"],
            Actions: ["request"]
        ).TryValidate(
            declared: declared,
            reason: out _
        ));
        Assert.True(condition: new WorldExports().TryValidate(
            declared: declared,
            reason: out _
        ));

        Assert.False(condition: new WorldExports(Reads: ["score", "score"]).TryValidate(
            declared: declared,
            reason: out var twice
        ));
        Assert.Equal(
            actual: twice,
            expected: "exports.reads lists 'score' twice."
        );

        Assert.False(condition: new WorldExports(Bindings: ["winner"]).TryValidate(
            declared: declared,
            reason: out var undeclared
        ));
        Assert.Equal(
            actual: undeclared,
            expected: "exports.bindings names 'winner', which the module does not declare."
        );

        Assert.False(condition: new WorldExports(Actions: [""]).TryValidate(
            declared: declared,
            reason: out var empty
        ));
        Assert.Contains(
            actualString: empty,
            expectedSubstring: "exports.actions carries an empty entry"
        );

        Assert.Equal(
            expected: "reads:score actions:request",
            actual: new WorldExports(
                Reads: ["score"],
                Actions: ["request"]
            ).Describe()
        );
        Assert.Equal(
            expected: "none",
            actual: new WorldExports().Describe()
        );
    }
    [Fact]
    public void TheRegistryAssignsTheFacetsHostBindingsBindThrough() {
        var facets = WorldNameRegistry.Sites.Where(predicate: static site => (site.Field.Member.Length > 0)).ToDictionary(
            keySelector: static site => site.Path,
            elementSelector: static site => site.Field.Facet,
            comparer: StringComparer.Ordinal
        );

        Assert.Equal(
            expected: WorldExportFacet.Action,
            actual: facets["interactions.interactions[].effects[][setState].state"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["interactions.interactions[].effects[][setState].fromState"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["interactions.interactions[].effects[][setState].key"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Action,
            actual: facets["kits.rows[].actions{*}.onPress.effects[][setState].state"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Action,
            actual: facets["placements.rows[].board.occupancy"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["placements.rows[].board.topology"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["placements.rows[].board.move"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["rules[].gate[compareState].state"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Action,
            actual: facets["rules[].effects[][generate].row"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Binding,
            actual: facets["hud.panels[].elements[].binding"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Binding,
            actual: facets["hud.panels[].elements[].template"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Binding,
            actual: facets["views.seatRig.operations[][selectProgram].key"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Read,
            actual: facets["exports.reads"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Action,
            actual: facets["exports.actions"]
        );
        Assert.Equal(
            expected: WorldExportFacet.Binding,
            actual: facets["exports.bindings"]
        );

        // Every binding-token and template site binds; every declaration site carries no facet of its own.
        Assert.All(
            collection: WorldNameRegistry.Sites.Where(predicate: static site => (site.Field.Role is WorldNameRole.Binding or WorldNameRole.Template)),
            action: static site => Assert.Equal(
                expected: WorldExportFacet.Binding,
                actual: site.Field.Facet
            )
        );
    }

    private const string Surface = /*lang=json*/ """{ "reads": [ "score" ], "actions": [ "request" ], "bindings": [ "score" ] }""";
}
