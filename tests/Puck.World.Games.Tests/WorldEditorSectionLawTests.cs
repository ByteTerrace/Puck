using Puck.Assets.Documents;
using Puck.Testing;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>editor</c> section changes nothing a world does until a builder uses it. Every
/// shipped world boots with the section absent, resolving to <see cref="WorldEditorDefaults.Default"/> (the grid hidden,
/// snapping off); every shipped world with the default authored still validates and writes the same bytes it reads
/// back; and an invalid value is refused by its document path.</summary>
public sealed class WorldEditorSectionLawTests : IDisposable {
    private readonly TemporaryDirectory m_staging = new();

    public static TheoryData<string> Shipped() => [.. WorldDocumentCorpus.ShippedDocuments().Order(comparer: StringComparer.Ordinal)];

    private static List<string> Errors(WorldDefinition definition) {
        var errors = new List<string>();

        _ = WorldDefinitionValidator.TryValidateLocally(
            compilation: out _,
            definition: definition,
            errors: errors
        );

        return errors;
    }

    public void Dispose() => m_staging.Dispose();
    [MemberData(nameof(Shipped))]
    [Theory]
    public void EveryShippedWorldBuildsFromTheDefaultAndRoundTripsItAuthored(string relativePath) {
        if (!WorldDocumentCorpus.TryBoot(
            path: RepositoryPaths.Resolve(relativePath: relativePath),
            reason: out _,
            stagingDirectory: Directory.CreateDirectory(path: m_staging.PathOf(name: Guid.NewGuid().ToString(format: "N"))).FullName,
            worlds: out var worlds
        )) {
            return;
        }

        foreach (var (source, definition) in worlds) {
            Assert.True(condition: (definition.EditorRaw is null), userMessage: $"{source} authors an editor section");
            Assert.Same(expected: WorldEditorDefaults.Default, actual: definition.Editor);
            Assert.False(condition: definition.Editor.ResolvedGrid.Visible);
            Assert.False(condition: definition.Editor.ResolvedSnap.Enabled);

            var authored = (definition with {
                EditorRaw = new WorldEditorDefaults(
                    Camera: new WorldEditorCamera(),
                    Grid: new WorldEditorGrid(),
                    Snap: new WorldEditorSnap()
                ),
            });
            var written = WorldDefinitionSerialization.Serialize(definition: authored);
            var read = WorldDefinitionSerialization.Deserialize(utf8Json: written);

            Assert.Equal(expected: authored.EditorRaw, actual: read.EditorRaw);
            Assert.Equal(expected: written, actual: WorldDefinitionSerialization.Serialize(definition: read));
        }
    }
    [Fact]
    public void AnInvalidEditorValueIsRefusedByItsPath() {
        var definition = Fixtures.BuildDocument();

        Assert.DoesNotContain(
            collection: Errors(definition: (definition with { EditorRaw = new WorldEditorDefaults(Grid: new WorldEditorGrid(Pitch: new DocumentVector3(x: 1f, y: 1f, z: 1f))) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.")
        );
        Assert.Contains(
            collection: Errors(definition: (definition with { EditorRaw = new WorldEditorDefaults(Grid: new WorldEditorGrid(Pitch: new DocumentVector3(x: 0.5f, y: 0f, z: 0.5f))) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.grid.pitch[1]")
        );
        Assert.Contains(
            collection: Errors(definition: (definition with { EditorRaw = new WorldEditorDefaults(Snap: new WorldEditorSnap(AngleStepDegrees: 0f)) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.snap.angleStepDegrees")
        );
        Assert.Contains(
            collection: Errors(definition: (definition with { EditorRaw = new WorldEditorDefaults(Camera: new WorldEditorCamera(MaxDistance: 0.25f, MinDistance: 0.5f)) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.camera.minDistance")
        );
    }
}
