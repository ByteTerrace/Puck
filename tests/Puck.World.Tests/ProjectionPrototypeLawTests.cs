using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Assets;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Projection references reuse recipient content across joins and worlds; a changed body costs one fetch.</summary>
public sealed class ProjectionPrototypeLawTests(ITestOutputHelper output) {
    private static WorldProjectionDelivery Compose(WorldProjectionFeed feed, WorldFixture fixture) => feed.Compose(
        definition: fixture.Server.Definition, authority: "boot", revision: 1,
        arena: fixture.Server.Arena, time: fixture.Server.DeliveryTime);
    private static void Hold(WorldProjectionHold hold, WorldProjectionDelivery delivery) {
        Assert.All(collection: WorldProjectionDelta.Tree(utf8Json: delivery.Payload)["prototypes"]!.AsArray(),
            action: row => Assert.False(condition: row!.AsObject().ContainsKey(propertyName: "document"), userMessage: "a projection carries prototype references, not inline bodies"));
        Assert.True(condition: hold.TryHold(utf8Json: delivery.Payload, definition: out _, reason: out var reason), userMessage: reason);
    }

    [Fact]
    public void A_rejoin_and_a_second_world_send_zero_prototype_bytes() {
        using var directory = new TemporaryDirectory(prefix: "puck-protoref-rejoin-");
        var source = new ContentAddressedStore(root: Path.Join(path1: directory.RootPath, path2: "source"));
        var cachePath = Path.Join(path1: directory.RootPath, path2: "recipient");
        var cache = new ContentAddressedStore(root: cachePath);
        var document = AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/moth-courtyard.puck");
        using var fixture = Fixtures.FreshServer(definition: WorldDefinitionSerialization.Deserialize(
            utf8Json: WorldDefinitionSerialization.SerializeCompact(definition: document)));

        byte[]? Fetch(ContentPin pin) => WorldProjectionPrototypeFetch.Fetch(server: fixture.Server, pin: pin,
            sourceAuthority: "viewer", ceiling: WorldDisclosureTier.Presentation, content: source);
        var arena = Fixtures.Store(definition: document);

        WorldProjectionDelivery Courtyard(WorldProjectionFeed feed) => feed.Compose(definition: document, authority: "boot",
            revision: 1, arena: arena, time: ArenaTime.At(engineTick: 0UL, tick: 0UL));
        var firstWork = new WorldProjectionWork();
        WorldProjectionDelivery first;

        using (WorldProjectionWork.Attribute(work: firstWork)) {
            first = Courtyard(feed: new WorldProjectionFeed(recipient: null, content: source));
            Assert.True(condition: WorldProjection.TryToDefinition(projection: first.Projection!, content: cache, fetch: Fetch,
                definition: out _, reason: out var reason), userMessage: reason);
        }
        Assert.True(condition: (firstWork.Read(kind: WorldProjectionWork.PrototypeFetches) > 0L));
        var inline = WorldProjectionDelta.Tree(utf8Json: first.Payload);

        inline["prototypes"] = new JsonArray(document.Creations.Select(selector: prototype => {
            var copy = JsonSerializer.Deserialize(utf8Json: JsonSerializer.SerializeToUtf8Bytes(value: prototype,
                jsonTypeInfo: WorldJsonContext.Default.WorldPrototype), jsonTypeInfo: WorldJsonContext.Default.WorldPrototype)!;

            Assert.True(condition: WorldStateDocumentValues.TryFlatten(graph: copy, reason: out var reason, source: document), userMessage: reason);
            return JsonNode.Parse(utf8Json: JsonSerializer.SerializeToUtf8Bytes(value: copy, jsonTypeInfo: WorldJsonContext.Default.WorldPrototype));
        }).ToArray());
        var before = CanonicalJsonDocument.SerializeCompact(node: inline).Length;
        var beforePrototypes = CanonicalJsonDocument.SerializeCompact(node: inline["prototypes"]!).Length;

        output.WriteLine(message: $"courtyard before: world.projection.bytes={before}; document leaf={(before + WorldFederationCodec.DocumentHeaderBytes)}; prototypes={beforePrototypes}");
        output.WriteLine(message: $"courtyard cold join: world.projection.bytes={firstWork.Read(kind: WorldProjectionWork.Bytes)}; prototype-fetches={firstWork.Read(kind: WorldProjectionWork.PrototypeFetches)}; prototype-bytes={firstWork.Read(kind: WorldProjectionWork.PrototypeBytes)}");

        var rejoinWork = new WorldProjectionWork();

        using (WorldProjectionWork.Attribute(work: rejoinWork)) {
            var rejoin = Courtyard(feed: new WorldProjectionFeed(recipient: null, content: source));
            var tree = WorldProjectionDelta.Tree(utf8Json: rejoin.Payload);

            Assert.All(collection: tree["prototypes"]!.AsArray(), action: row => Assert.False(condition: row!.AsObject().ContainsKey(propertyName: "document"), userMessage: "a rejoin must not carry an inline prototype body"));
            Hold(hold: new WorldProjectionHold(content: new ContentAddressedStore(root: cachePath), fetch: Fetch), delivery: rejoin);
            var secondWorld = document with { Metadata = new WorldMetadataSection(Title: "another world") };
            var secondFeed = new WorldProjectionFeed(recipient: null, content: source);
            var second = secondFeed.Compose(definition: secondWorld, arena: fixture.Server.Arena, authority: "other", revision: 1,
                time: fixture.Server.DeliveryTime);

            Hold(hold: new WorldProjectionHold(content: cache, fetch: Fetch), delivery: second);
        }
        Assert.Equal(expected: 0L, actual: rejoinWork.Read(kind: WorldProjectionWork.PrototypeBytes));
        Assert.Equal(expected: 0L, actual: rejoinWork.Read(kind: WorldProjectionWork.PrototypeFetches));
        output.WriteLine(message: $"courtyard rejoin: world.projection.bytes={first.Payload.Length}; document leaf={(first.Payload.Length + WorldFederationCodec.DocumentHeaderBytes)}; prototype-fetches=0; prototype-bytes=0");
        Assert.Null(@object: Fetch(pin: source.Put(content: "undisclosed"u8)));
        Assert.Null(@object: WorldProjectionPrototypeFetch.Fetch(server: fixture.Server,
            pin: ContentPin.Parse(text: first.Projection!.Creations[0].Content), sourceAuthority: "viewer",
            ceiling: WorldDisclosureTier.Frames, content: source));
    }
    [Fact]
    public void A_changed_prototype_fetches_one_body_and_a_bad_pin_preserves_the_hold() {
        using var directory = new TemporaryDirectory(prefix: "puck-protoref-change-");
        var source = new ContentAddressedStore(root: Path.Join(path1: directory.RootPath, path2: "source"));
        var cache = new ContentAddressedStore(root: Path.Join(path1: directory.RootPath, path2: "recipient"));
        var document = Fixtures.BuildDocument() with { CreationsRaw = [CreationFixtures.Sphere(id: "one", scale: 1f), CreationFixtures.Sphere(id: "two", scale: 2f)] };
        using var fixture = Fixtures.FreshServer(definition: document);

        byte[]? Fetch(ContentPin pin) => WorldProjectionPrototypeFetch.Fetch(server: fixture.Server, pin: pin,
            sourceAuthority: "viewer", ceiling: WorldDisclosureTier.Presentation, content: source);
        var feed = new WorldProjectionFeed(recipient: null, content: source);
        var hold = new WorldProjectionHold(content: cache, fetch: Fetch);
        var initial = Compose(feed: feed, fixture: fixture);

        Hold(delivery: initial, hold: hold);
        var oldPin = ContentPin.Parse(text: initial.Projection!.Creations.Single(predicate: row => (row.Id.Value == "one")).Content);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertCreation(Principal: Principal.Console,
            Creation: CreationFixtures.Sphere(id: "one", scale: 3f)));
        fixture.Step();
        var work = new WorldProjectionWork();

        using (WorldProjectionWork.Attribute(work: work)) {
            var changed = Compose(feed: feed, fixture: fixture);

            Assert.Equal(expected: WorldProjectionDeliveryKind.Delta, actual: changed.Kind);
            Assert.DoesNotContain(expectedSubstring: "\"document\"", actualString: System.Text.Encoding.UTF8.GetString(bytes: changed.Payload));
            Assert.True(condition: hold.TryApply(utf8Json: changed.Payload, definition: out _, valuesOnly: out _, timelineOnly: out _, reason: out var reason), userMessage: reason);
        }
        Assert.Equal(expected: 1L, actual: work.Read(kind: WorldProjectionWork.PrototypeFetches));
        Assert.Equal(expected: WorldProjectionContent.Serialize(prototype: fixture.Server.Definition.Creations.Single(predicate: row => (row.Id.Value == "one"))).Length,
            actual: work.Read(kind: WorldProjectionWork.PrototypeBytes));
        Assert.Equal(expected: WorldProjectionDeliveryKind.None, actual: Compose(feed: feed, fixture: fixture).Kind);
        Assert.True(condition: source.Contains(pin: oldPin));
        Assert.Null(@object: Fetch(pin: oldPin));
        var prior = hold.Definition;
        var pin = ContentPin.Compute(content: "not fetched"u8);
        var forged = new JsonObject { ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "one", ["content"] = pin.ToString() }) };

        Assert.False(condition: hold.TryApply(utf8Json: CanonicalJsonDocument.SerializeCompact(node: forged), definition: out _, valuesOnly: out _, timelineOnly: out _, reason: out _));
        Assert.Same(expected: prior, actual: hold.Definition);
        Assert.False(condition: cache.Contains(pin: pin));
    }
}
