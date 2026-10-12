using System.Text.Json.Nodes;

using Puck.World.Transpiler;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: a <c>captures</c> section that names no directory writes under the run's state root, never
/// under the working directory, and no shipped world names one. CONTROL: a section that does name a relative
/// directory resolves it against the working directory, so the first assertion is about the default and not about
/// every path landing under the state root.</summary>
public sealed class WorldCapturesDirectoryLawTests {
    private static readonly string[] ShippedCaptureWorlds = [
        "src/Puck.World/Assets/worlds/puck.world.json",
        "tests/Puck.Parity/parity.puck",
        "tests/Puck.Parity/parity-inside.puck",
        "tests/Puck.Parity/paths.puck",
    ];

    // A shipped capture world as the game reads it: a .puck source's compiled document, or the JSON document itself.
    private static JsonObject? Document(string world) {
        var path = RepositoryPaths.Resolve(relativePath: world);

        return (WorldDocumentName.IsSourceFile(path: path)
            ? WorldCompiler.CompileFile(path: path).RequireJson()
            : (JsonNode.Parse(json: File.ReadAllText(path: path)) as JsonObject)
        );
    }
    [Fact]
    public void NoShippedWorldNamesACaptureDirectory() {
        foreach (var world in ShippedCaptureWorlds) {
            var captures = (Document(world: world)!["captures"] as JsonObject);

            Assert.NotNull(@object: captures);
            Assert.False(
                condition: captures.ContainsKey(propertyName: "directory"),
                userMessage: $"{world} names captures.directory, so a boot of it writes its captures wherever it is started."
            );
        }
    }
}
