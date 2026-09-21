using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldPoolIntegrationLawTests {
    private static CellName Name(string text) => CellName.Parse(candidate: text);
    private static WorldDefinition Document(bool seeded = false) {
        var document = Fixtures.BuildDocument();

        return document with {
            StateRaw = document.StateRaw! with {
                Records = [new StateRecord(Name: Name(text: "actor"), Fields: [
                new StatePoolField(Name: Name(text: "score"), Default: CellValue.Int(value: 7)),
                new StatePoolField(Name: Name(text: "label"), Kind: CellKind.Text, Default: CellValue.Text(value: "ready"))
            ])],
                Pools = [new StatePool(Name: Name(text: "actors"), Record: Name(text: "actor"), Capacity: 3,
                Initial: (seeded ? [new StatePoolSeed(Slot: 0)] : null))],
            },
        };
    }

    [Fact]
    public void SerializationRetainsDeclarationsAndNeverPublishesGeneratedRows() {
        var source = Document(seeded: true);
        var bytes = WorldDefinitionSerialization.Serialize(definition: source);
        var wire = JsonNode.Parse(bytes)!;

        Assert.Equal(source.AuthoredState.Count, wire["state"]!["world"]!.AsArray().Count);
        var restored = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        Assert.Equal(source.State.Count, restored.State.Count);
        Assert.Equal(source.StateCatalog.Pools.Count, restored.StateCatalog.Pools.Count);
        Assert.Equal("ready", restored.StateCatalog.Pools[0].Fields[1].Default.AsText);
    }
    [Fact]
    public void ValueOnlySnapshotReusesShapeButExpandsCurrentValues() {
        var source = Document();
        var catalog = source.StateCatalog;
        var arena = new StateArena(catalog: catalog, section: source.StateRaw, time: ArenaTime.Origin);

        Assert.True(condition: arena.TryClaim(handle: out var handle, poolOrdinal: 0, reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryWrite(handle, 0, CellValue.Int(value: 23), out reason), userMessage: reason);
        var updated = source.WithWorldState(rows: source.AuthoredState, pools: arena.ToPools());

        Assert.Same(catalog, updated.StateCatalog);
        var restored = new StateArena(catalog: updated.StateCatalog, section: updated.StateRaw, time: ArenaTime.Origin);

        Assert.True(condition: restored.TryRead(fieldOrdinal: 0, handle: handle, value: out var value));
        Assert.Equal(23L, value.AsInt);
        Assert.Equal(arena.ComputeHash(), restored.ComputeHash());
    }
    [Fact]
    public void ServerPublishesPoolOnlyChangesAndPreservesReleasedGeneration() {
        var document = Document(seeded: true) with {
            Rules = [new WorldRule(
            Name: Name(text: "remove"), Mode: ActionTriggerMode.Edge,
            Effects: [new ActionEffect.ForEachPool(Pool: "actors", Binding: Name(text: "x"), Effects: [new ActionEffect.Release(Binding: Name(text: "x"))])]
        )],
        };
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Step();
        var snapshot = Assert.Single(collection: fixture.Server.Definition.StateRaw!.Pools!).Snapshot;

        Assert.NotNull(@object: snapshot);
        Assert.Empty(collection: snapshot.Live!);
        Assert.Equal(1L, snapshot.Generations[0]);
        var restored = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: fixture.Server.Definition));
        var arena = new StateArena(catalog: restored.StateCatalog, section: restored.StateRaw, time: ArenaTime.Origin);

        Assert.True(condition: arena.TryClaim(handle: out var reclaimed, poolOrdinal: 0, reason: out var reason), userMessage: reason);
        Assert.Equal(1L, reclaimed.Generation);
    }
    [Fact]
    public void DeclarationHashIncludesDefaultsButNotPublicationRepresentation() {
        var source = Document(seeded: true);
        var arena = new StateArena(catalog: source.StateCatalog, section: source.StateRaw, time: ArenaTime.Origin);
        var published = source.WithWorldState(rows: source.AuthoredState, pools: arena.ToPools());

        static ulong Hash(WorldDefinition definition) {
            var hash = Fnv1aHash.Create();

            WorldStateHashComposition.AppendDeclaration(hash: ref hash, state: definition.StateRaw);
            return hash.Value;
        }
        Assert.Equal(Hash(definition: source), Hash(definition: published));
        var changed = source with {
            StateRaw = source.StateRaw! with {
                Records = [source.StateRaw!.Records![0] with { Fields = [new StatePoolField(Name: Name(text: "score"), Default: CellValue.Int(value: 9)), source.StateRaw!.Records![0].Fields![1]] }],
            },
        };

        Assert.NotEqual(Hash(definition: source), Hash(definition: changed));
    }
    [Fact]
    public void AliasedModuleRenamesPoolsAndRecordsButPreservesLocalBindings() {
        var node = JsonNode.Parse("""
            {"state":{"records":[{"name":"actor","fields":[{"name":"score"}]}],"pools":[{"name":"actors","record":"actor","capacity":3}]},
             "rules":[{"name":"spawn","effects":[{"$type":"claim","pool":"actors","binding":"x","effects":[{"$type":"setState","row":"x.score","value":7}]}]}]}
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "a", module: node, reason: out var reason), userMessage: reason);
        Assert.Equal("a_actor", node["state"]!["records"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("a_actors", node["rules"]![0]!["effects"]![0]!["pool"]!.GetValue<string>());
        Assert.Equal("x", node["rules"]![0]!["effects"]![0]!["binding"]!.GetValue<string>());
        Assert.Equal("x.score", node["rules"]![0]!["effects"]![0]!["effects"]![0]!["row"]!.GetValue<string>());
    }
    [Fact]
    public void AliasedStaticPoolFieldsRenameWhileShadowingInstanceBindingsStayLocal() {
        var node = JsonNode.Parse("""
            {"state":{"records":[{"name":"actor","fields":[{"name":"score"}]}],"pools":[{"name":"actors","record":"actor","capacity":3}]},
             "rules":[{"name":"award","effects":[
                {"$type":"setState","state":"actors.score","key":"0","value":7},
                {"$type":"claim","pool":"actors","binding":"actors","effects":[{"$type":"setState","state":"actors.score","value":9}]}]}]}
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "a", module: node, reason: out var reason), userMessage: reason);
        var effects = node["rules"]![0]!["effects"]!;

        Assert.Equal("a_actors.score", effects[0]!["state"]!.GetValue<string>());
        Assert.Equal("a_actors", effects[1]!["pool"]!.GetValue<string>());
        Assert.Equal("actors.score", effects[1]!["effects"]![0]!["state"]!.GetValue<string>());
    }
}
