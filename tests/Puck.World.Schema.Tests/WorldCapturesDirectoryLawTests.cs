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
        "tests/Puck.Parity/parity.world.json",
        "tests/Puck.Parity/parity-inside.world.json",
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
    private static WorldCapturesSection Section(string? directory) => new(
        Directory: directory,
        Rows: [new WorldCaptureRow(
            Palette: [new WorldCapturePaletteEntry(
                Color: "#000000",
                Material: 0
            )],
            Station: CellName.Parse(candidate: "station"),
            Ticks: [5UL]
        )]
    );
    private static bool IsUnder(string path, string directory) => Path.GetFullPath(path: path).StartsWith(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: (Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: directory)) + Path.DirectorySeparatorChar)
    );
    private static List<string> Validate(WorldCapturesSection captures) {
        var errors = new List<string>();

        _ = WorldDefinitionValidator.TryValidateLocally(
            new WorldDefinition() with { Captures = captures },
            errors,
            out _
        );

        return errors;
    }

    [Fact]
    public void AnUnnamedDirectoryIsUnderTheStateRootAndANamedOneIsBesideTheDocument() {
        var stateRoot = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-state-" + Guid.NewGuid().ToString(format: "N"))
        );
        var resolved = Section(directory: null).ResolveDirectory(
            documentDirectory: null,
            stateRoot: stateRoot
        );

        Assert.Equal(
            actual: resolved,
            expected: Path.Combine(
                path1: stateRoot,
                path2: WorldCapturesSection.DefaultDirectoryName
            )
        );
        Assert.False(condition: IsUnder(
            directory: Environment.CurrentDirectory,
            path: resolved
        ));

        var documentDirectory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-document-" + Guid.NewGuid().ToString(format: "N"))
        );
        var authored = Section(directory: "captures").ResolveDirectory(
            documentDirectory: documentDirectory,
            stateRoot: stateRoot
        );

        Assert.True(condition: IsUnder(
            directory: documentDirectory,
            path: authored
        ));
        Assert.False(condition: IsUnder(
            directory: Environment.CurrentDirectory,
            path: authored
        ));
        Assert.False(condition: IsUnder(
            directory: stateRoot,
            path: authored
        ));
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
    [Fact]
    public void AnAbsentDirectoryValidatesAndABlankOneIsRefusedByName() {
        Assert.DoesNotContain(
            collection: Validate(captures: Section(directory: null)),
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "captures.directory"
            )
        );
        Assert.Contains(
            collection: Validate(captures: Section(directory: " ")),
            filter: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "captures.directory is blank"
            )
        );
    }
}
