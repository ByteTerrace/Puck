using Xunit;

using Puck.World.Authoring;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>tests/Puck.Parity/parity.world.json</c> — the parity vocabulary station's authored world — validates
/// locally through the same pipeline every other world document does (<see cref="WorldDefinitionSerialization.Deserialize"/>,
/// <see cref="WorldDefinitionValidator.TryValidateLocally(WorldDefinition, out string)"/>), and its <c>vocabRig</c>
/// prototype's panelled shapes (the Prism and the Cylinder, each carrying a <see cref="ShapePanelDocument"/>)
/// double-charge <see cref="CreationDocument.StampShapeCount"/> exactly as the per-copy instance reservation
/// (<c>CreationStampEmitter.PerCopyInstanceCount</c>) and the render envelope probe expect.
/// </summary>
public sealed class ParityVocabularyStationLawTests {
    private const string PrototypeId = "vocabRig";

    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static WorldDefinition LoadParityWorld() {
        var path = Path.Combine(RepoRoot(), "tests", "Puck.Parity", "parity.world.json");
        var bytes = File.ReadAllBytes(path: path);

        return WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
    }

    /// <summary>The subject: the authored document round-trips through deserialize+migrate+validate with no
    /// refusal, and re-validating the already-deserialized definition (the same call <c>Deserialize</c> makes
    /// internally) holds too, naming the reason on failure rather than an opaque exception.</summary>
    [Fact]
    public void TheParityWorldDocumentValidatesLocally() {
        var definition = LoadParityWorld();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason),
            userMessage: reason
        );
    }

    /// <summary>The subject: the vocabulary rig's two panelled shapes (panelPrism, panelCylinder) each charge 2
    /// toward the per-stamp shape budget; its four plain shapes (chamferedBox, wingHinge, wingPanel, repeatPlate)
    /// charge 1 each.</summary>
    [Fact]
    public void TheVocabularyRigDoubleChargesEveryPanelledShape() {
        var definition = LoadParityWorld();
        var prototype = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: PrototypeId);

        Assert.NotNull(@object: prototype);

        var shapes = (prototype!.Document.Shapes ?? []);
        var panelledCount = shapes.Count(predicate: static shape => (shape.Panel is not null));

        Assert.Equal(expected: 6, actual: shapes.Count);
        Assert.Equal(expected: 2, actual: panelledCount);
        Assert.Equal(
            expected: (shapes.Count + panelledCount),
            actual: prototype.Document.StampShapeCount()
        );
        Assert.Equal(expected: 8, actual: prototype.Document.StampShapeCount());
    }

    /// <summary>The control: a copy of the rig with every panel stripped falls back to one charge per shape — the
    /// double charge is the panel's doing, not a property of the shape count alone.</summary>
    [Fact]
    public void StrippingEveryPanelDropsTheChargeBackToOnePerShape() {
        var definition = LoadParityWorld();
        var prototype = WorldDefinitionRows.FindCreation(creations: definition.Creations, id: PrototypeId)!;
        var shapes = (prototype.Document.Shapes ?? []);
        var unpanelled = shapes.Select(selector: static shape => (shape.Panel is null
            ? shape
            : (shape with { Panel = null })
        )).ToList();
        var document = (prototype.Document with { Shapes = unpanelled });

        Assert.Equal(expected: unpanelled.Count, actual: document.StampShapeCount());
    }
}
