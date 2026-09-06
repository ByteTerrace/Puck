using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The export record validates against what a module declares, the compose-time check admits a reference
/// only through the export list the registry assigns the referencing field, and the registry assigns the facets
/// a host's kit, placement, rule, and HUD bindings bind through.</summary>
public sealed class WorldExportsLawTests {
    private static JsonObject Module(string exports) => ((JsonObject)JsonNode.Parse(json: $$"""
        {
          "exports": {{exports}},
          "state": { "world": [ { "name": "request", "kind": "int", "value": 0 }, { "name": "score", "kind": "int", "value": 0 } ],
                     "lattices": [ { "$type": "grid", "name": "grid", "width": 2, "depth": 2, "cellSize": 1 } ] },
          "rules": [ { "name": "settle", "effects": [ { "$type": "setState", "state": "score", "fromState": "request" } ] } ]
        }
        """)!);
    private static JsonObject Host(string body) => ((JsonObject)JsonNode.Parse(json: body)!);
    private const string Surface = /*lang=json*/ """{ "reads": [ "score" ], "actions": [ "request" ], "bindings": [ "score" ] }""";

    private static bool TryTake(JsonObject module, out WorldModuleSurface? surface, out string reason) {
        surface = null;

        if (!WorldModuleExports.TryTake(module: module, moduleName: "module", exports: out var exports, reason: out reason)) {
            return false;
        }

        if (!WorldModuleExports.TryCheckLayers(hostPath: "host", ownBody: [], basis: [], imports: [("module", module, exports)], surfaces: out var surfaces, reason: out reason)) {
            return false;
        }

        surface = surfaces.Single();

        return true;
    }
    private static WorldModuleSurface Take(JsonObject module) {
        Assert.True(condition: TryTake(module: module, surface: out var surface, reason: out var reason), userMessage: reason);

        return surface!;
    }
    // Checks a host's own body against the module authored under Surface.
    private static bool TryCheck(JsonObject tree, string treeName, out string reason) {
        var module = Module(exports: Surface);

        Assert.True(condition: WorldModuleExports.TryTake(module: module, moduleName: "module", exports: out var exports, reason: out reason), userMessage: reason);

        return WorldModuleExports.TryCheckLayers(hostPath: treeName, ownBody: tree, basis: [], imports: [("module", module, exports!)], surfaces: out _, reason: out reason);
    }

    [Fact]
    public void TheRecordValidatesAgainstTheModulesDeclarations() {
        var declared = new HashSet<string>(collection: ["request", "score"], comparer: StringComparer.Ordinal);

        Assert.True(condition: new WorldExports(Reads: ["score"], Actions: ["request"]).TryValidate(declared: declared, reason: out _));
        Assert.True(condition: new WorldExports().TryValidate(declared: declared, reason: out _));

        Assert.False(condition: new WorldExports(Reads: ["score", "score"]).TryValidate(declared: declared, reason: out var twice));
        Assert.Equal(expected: "exports.reads lists 'score' twice.", actual: twice);

        Assert.False(condition: new WorldExports(Bindings: ["winner"]).TryValidate(declared: declared, reason: out var undeclared));
        Assert.Equal(expected: "exports.bindings names 'winner', which the module does not declare.", actual: undeclared);

        Assert.False(condition: new WorldExports(Actions: [""]).TryValidate(declared: declared, reason: out var empty));
        Assert.Contains(expectedSubstring: "exports.actions carries an empty entry", actualString: empty);

        Assert.Equal(expected: "reads:score actions:request", actual: new WorldExports(Reads: ["score"], Actions: ["request"]).Describe());
        Assert.Equal(expected: "none", actual: new WorldExports().Describe());
    }
    [Fact]
    public void TakingTheExportsStripsTheMemberAndValidatesEachEntry() {
        var module = Module(exports: /*lang=json*/ """{ "reads": [ "score", "grid" ], "actions": [ "request" ] }""");
        var surface = Take(module: module);

        Assert.False(condition: module.ContainsKey(propertyName: WorldExports.MemberName));
        Assert.True(condition: surface.Declares(name: "request"));
        Assert.True(condition: surface.Declares(name: "settle"));
        Assert.True(condition: surface.Admits(name: "score", facet: WorldExportFacet.Read));
        Assert.True(condition: surface.Admits(name: "grid", facet: WorldExportFacet.Read));
        Assert.True(condition: surface.Admits(name: "request", facet: WorldExportFacet.Action));
        Assert.False(condition: surface.Admits(name: "request", facet: WorldExportFacet.Read));
        Assert.False(condition: surface.Admits(name: "score", facet: WorldExportFacet.Action));
        Assert.False(condition: surface.Admits(name: "settle", facet: WorldExportFacet.Read));
        Assert.True(condition: surface.Admits(name: "elsewhere", facet: WorldExportFacet.Action));

        Assert.False(condition: TryTake(module: Module(exports: /*lang=json*/ """{ "reads": [ "winner" ] }"""), surface: out _, reason: out var undeclared));
        Assert.Equal(expected: "host imports module: exports.reads names 'winner', which the module does not declare.", actual: undeclared);

        Assert.False(condition: TryTake(module: Module(exports: /*lang=json*/ """{ "writes": [ "request" ] }"""), surface: out _, reason: out var malformed));
        Assert.Contains(expectedSubstring: "module: 'exports' is malformed", actualString: malformed);

        Assert.True(condition: TryTake(module: Module(exports: "null"), surface: out var closed, reason: out _));
        Assert.False(condition: closed!.Admits(name: "score", facet: WorldExportFacet.Read));
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

        Assert.True(condition: TryCheck(tree: admitted, treeName: "host", reason: out var admittedReason), userMessage: admittedReason);

        var writesARead = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "score", "value": 1 } ] } ] }""");

        Assert.False(condition: TryCheck(tree: writesARead, treeName: "host", reason: out var writeReason));
        Assert.Equal(expected: "host names 'score' at $.rules[0].effects[0].state, which module declares and does not export under exports.actions; a host binds only what its module exports.", actual: writeReason);

        var readsAnAction = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "own", "expression": "request + 1" } ] } ], "state": { "world": [ { "name": "own", "kind": "int", "value": 0 } ] } }""");

        Assert.False(condition: TryCheck(tree: readsAnAction, treeName: "host", reason: out var readReason));
        Assert.Contains(expectedSubstring: "'request' at $.rules[0].effects[0].expression", actualString: readReason);
        Assert.Contains(expectedSubstring: "exports.reads", actualString: readReason);

        var bindsAPrivateRow = Host(body: /*lang=json*/ """{ "hud": { "panels": [ { "id": "p", "layer": "Under", "style": "Strip", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "elements": [ { "id": "e", "kind": "Gauge", "style": "Primary", "rect": { "x": 0, "y": 0, "width": 1, "height": 1 }, "binding": "state.request" } ] } ] } }""");

        Assert.False(condition: TryCheck(tree: bindsAPrivateRow, treeName: "host", reason: out var bindReason));
        Assert.Contains(expectedSubstring: "'request' at $.hud.panels[0].elements[0].binding", actualString: bindReason);
        Assert.Contains(expectedSubstring: "exports.bindings", actualString: bindReason);

        var keyIndirection = Host(body: /*lang=json*/ """{ "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "own", "key": "$cell:request:$value", "value": 1 } ] } ], "state": { "world": [ { "name": "own", "kind": "int", "capacity": 4 } ] } }""");

        Assert.False(condition: TryCheck(tree: keyIndirection, treeName: "host", reason: out var keyReason));
        Assert.Contains(expectedSubstring: "'request' at $.rules[0].effects[0].key", actualString: keyReason);

        // A module's own references, and a name the checked layer itself declares, are never checked.
        var selfReferencing = Module(exports: Surface);

        selfReferencing[propertyName: "rules"]!.AsArray().Add(value: JsonNode.Parse(json: /*lang=json*/ """{ "name": "reset", "effects": [ { "$type": "setState", "state": "score", "value": 0 } ] }"""));
        Assert.True(condition: WorldModuleExports.TryTake(module: selfReferencing, moduleName: "module", exports: out var selfExports, reason: out var selfReason), userMessage: selfReason);
        Assert.True(condition: WorldModuleExports.TryCheckLayers(hostPath: "host", ownBody: [], basis: [], imports: [("module", selfReferencing, selfExports!)], surfaces: out _, reason: out selfReason), userMessage: selfReason);

        var restates = Host(body: /*lang=json*/ """{ "state": { "world": [ { "name": "score", "kind": "int", "value": 5 } ] }, "rules": [ { "name": "host", "effects": [ { "$type": "setState", "state": "score", "value": 1 } ] } ] }""");

        Assert.True(condition: TryCheck(tree: restates, treeName: "host", reason: out _));
    }
    [Fact]
    public void TheRegistryAssignsTheFacetsHostBindingsBindThrough() {
        var facets = WorldNameRegistry.Sites.Where(predicate: static site => (site.Field.Member.Length > 0)).ToDictionary(keySelector: static site => site.Path, elementSelector: static site => site.Field.Facet, comparer: StringComparer.Ordinal);

        Assert.Equal(expected: WorldExportFacet.Action, actual: facets["interactions.interactions[].effects[][setState].state"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["interactions.interactions[].effects[][setState].fromState"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["interactions.interactions[].effects[][setState].key"]);
        Assert.Equal(expected: WorldExportFacet.Action, actual: facets["kits.rows[].actions{*}.onPress.effects[][setState].state"]);
        Assert.Equal(expected: WorldExportFacet.Action, actual: facets["placements.rows[].board.occupancy"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["placements.rows[].board.topology"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["placements.rows[].board.move"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["rules[].gate[compareState].state"]);
        Assert.Equal(expected: WorldExportFacet.Action, actual: facets["rules[].effects[][generate].row"]);
        Assert.Equal(expected: WorldExportFacet.Binding, actual: facets["hud.panels[].elements[].binding"]);
        Assert.Equal(expected: WorldExportFacet.Binding, actual: facets["hud.panels[].elements[].template"]);
        Assert.Equal(expected: WorldExportFacet.Binding, actual: facets["views.seatRig.operations[][select].key"]);
        Assert.Equal(expected: WorldExportFacet.Read, actual: facets["exports.reads"]);
        Assert.Equal(expected: WorldExportFacet.Action, actual: facets["exports.actions"]);
        Assert.Equal(expected: WorldExportFacet.Binding, actual: facets["exports.bindings"]);

        // Every binding-token and template site binds; every declaration site carries no facet of its own.
        Assert.All(collection: WorldNameRegistry.Sites.Where(predicate: static site => (site.Field.Role is WorldNameRole.Binding or WorldNameRole.Template)), action: static site => Assert.Equal(expected: WorldExportFacet.Binding, actual: site.Field.Facet));
    }
}
