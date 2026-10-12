using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Assets;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>Projection references reuse recipient content across joins and worlds; a changed body costs one fetch.</summary>
public sealed class ProjectionPrototypeLawTests {
    private static WorldProjectionDelivery Compose(WorldProjectionFeed feed, WorldFixture fixture) => feed.Compose(
        definition: fixture.Server.Definition, authority: "boot", revision: 1,
        arena: fixture.Server.Arena, time: fixture.Server.DeliveryTime);
    private static void Hold(WorldProjectionHold hold, WorldProjectionDelivery delivery) {
        Assert.All(collection: WorldProjectionDelta.Tree(utf8Json: delivery.Payload)["prototypes"]!.AsArray(),
            action: row => Assert.False(condition: row!.AsObject().ContainsKey(propertyName: "document"), userMessage: "a projection carries prototype references, not inline bodies"));
        Assert.True(condition: hold.TryHold(utf8Json: delivery.Payload, definition: out _, reason: out var reason), userMessage: reason);
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
