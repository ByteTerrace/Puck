using System.Text.Json.Nodes;

using Puck.Testing;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the owned-world catalog's one read of its files: a file is admitted through the loader's
/// drawing door, drawn for the owned world's own id, so a Boot-timing draw site the file leaves empty holds its
/// drawn cell once admitted, and the same bytes under the same id draw the same value.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class OwnedWorldDrawLawTests {
    private const string DrawnRow = "strideCadence";

    // Seeds a catalog, then adds one Boot-timing draw site with no cell to the first seeded owned world's file.
    private static (string FileName, string Id) SeedWithUndrawnSite(TemporaryDirectory dir) {
        var seeded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var id = seeded.All[0].Id;
        var fileName = WorldDocumentName.For(id: Puck.State.SafeName.Parse(candidate: id));
        var path = Path.Combine(path1: dir.RootPath, path2: fileName);
        var node = JsonNode.Parse(json: File.ReadAllText(path: path))!.AsObject();
        var state = (node["state"] ??= new JsonObject()).AsObject();
        var world = (state["world"] ??= new JsonArray()).AsArray();

        world.Add(item: JsonNode.Parse(json: $$"""
            {
              "draw": {
                "generator": { "rangeMax": 524288, "rangeMin": 327680, "source": "UniformRange" },
                "timing": "Boot"
              },
              "kind": "Fixed",
              "name": "{{DrawnRow}}"
            }
            """));
        File.WriteAllText(path: path, contents: node.ToJsonString());

        return (fileName, id);
    }
    private static Puck.State.StateCell DrawnCell(WorldOwnedWorlds catalog, string id) {
        var identity = Assert.Single(collection: catalog.All, predicate: candidate => (candidate.Id == id));
        var row = Assert.Single(collection: identity.Document!.AuthoredState, predicate: candidate => (candidate.Name.Value == DrawnRow));

        return Assert.Single(collection: row.Cells!);
    }

    [Fact]
    public void AnOwnedWorldsEmptyDrawSiteIsDrawnWhenTheCatalogAdmitsIt() {
        using var dir = new TemporaryDirectory();

        var (_, id) = SeedWithUndrawnSite(dir: dir);
        var catalog = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );

        Assert.Empty(collection: catalog.Refused);
        Assert.Empty(collection: catalog.Discarded);
        _ = DrawnCell(catalog: catalog, id: id);
    }
    [Fact]
    public void TheSameOwnedWorldBytesDrawTheSameValue() {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();

        var (fileName, id) = SeedWithUndrawnSite(dir: first);

        File.Copy(
            destFileName: Path.Combine(path1: second.RootPath, path2: fileName),
            sourceFileName: Path.Combine(path1: first.RootPath, path2: fileName)
        );

        var a = new WorldOwnedWorlds(directory: first.RootPath, machineId: Guid.NewGuid(), template: Fixtures.BuildDocument());
        var b = new WorldOwnedWorlds(directory: second.RootPath, machineId: Guid.NewGuid(), template: Fixtures.BuildDocument());

        Assert.Equal(
            actual: DrawnCell(catalog: b, id: id),
            expected: DrawnCell(catalog: a, id: id)
        );
    }
}
